import {
  BadRequestException, ConflictException,
  HttpException, HttpStatus, Inject, Injectable, Logger,
  ServiceUnavailableException, UnsupportedMediaTypeException,
  CallHandler, ExecutionContext, NestInterceptor,
} from "@nestjs/common";
import { Reflector } from "@nestjs/core";
import { defer, lastValueFrom, Observable } from "rxjs";
import {
  computeRequestFingerprint, computeScopeKey,
} from "@idempotencykit/core";
import type { StoredHttpResponse } from "@idempotencykit/core";
import { IDEMPOTENCY_HTTP_OPTIONS } from "./idempotency-options.js";
import type { IdempotencyKitOptions, IdempotencyHttpRequest } from "./idempotency-options.js";
import {
  IDEMPOTENT_ROUTE_METADATA, IdempotentRouteMetadata,
} from "./idempotent.decorator.js";

const MAX_REQUEST_BYTES = 65_536;
const MAX_RESPONSE_BYTES = 262_144;
const SAFE_HEADERS = ["location", "etag", "content-language"] as const;
const STRICT_UTF8 = new TextDecoder("utf-8", { fatal: true });

interface HttpResponse {
  statusCode: number;
  headersSent?: boolean;
  setHeader(name: string, value: string): void;
  getHeader(name: string): number | string | string[] | undefined;
}

@Injectable()
export class IdempotencyInterceptor implements NestInterceptor {
  private readonly logger = new Logger(IdempotencyInterceptor.name);

  constructor(
    @Inject(IDEMPOTENCY_HTTP_OPTIONS) private readonly options: IdempotencyKitOptions,
    private readonly reflector: Reflector,
  ) {}

  intercept(context: ExecutionContext, next: CallHandler): Observable<unknown> {
    const route = this.reflector.getAllAndOverride<IdempotentRouteMetadata>(
      IDEMPOTENT_ROUTE_METADATA, [context.getHandler(), context.getClass()],
    );
    if (!route) return next.handle();

    // Guards have run by the time interceptors execute.
    return defer(async () => this.process(context, next, route));
  }

  private async process(
    context: ExecutionContext, next: CallHandler, route: IdempotentRouteMetadata,
  ): Promise<unknown> {
    const req = context.switchToHttp().getRequest<IdempotencyHttpRequest>();
    const res = context.switchToHttp().getResponse<HttpResponse>();

    if (!res || typeof res.setHeader !== "function" || typeof res.getHeader !== "function")
      throw new ServiceUnavailableException("Only the NestJS Express adapter is supported in v1.");
    if (!req.user)
      throw new HttpException({ code: "IDEMPOTENCY_AUTH_REQUIRED" },
        HttpStatus.UNAUTHORIZED);
    if (!["POST", "PUT", "PATCH"].includes(req.method?.toUpperCase()))
      throw new HttpException({ code: "IDEMPOTENCY_METHOD_UNSUPPORTED" },
        HttpStatus.METHOD_NOT_ALLOWED);

    const key = req.headers["idempotency-key"];
    if (typeof key !== "string" || !key)
      throw new BadRequestException({ code: "IDEMPOTENCY_KEY_REQUIRED" });

    // Reject multiple header entries, including duplicates combined by Node.
    const raw = req.rawHeaders ?? [];
    const keyCount = raw.filter((value, index) =>
      index % 2 === 0 && value.toLowerCase() === "idempotency-key").length;
    if (raw.length && keyCount !== 1)
      throw new BadRequestException({ code: "IDEMPOTENCY_KEY_REQUIRED" });

    const encoding = req.headers["content-encoding"];
    if (encoding && encoding !== "identity")
      throw new UnsupportedMediaTypeException({ code: "IDEMPOTENCY_CONTENT_ENCODING_UNSUPPORTED" });

    const contentType = req.headers["content-type"];
    if (contentType && (typeof contentType !== "string" ||
        contentType.length > 128 || /[\r\n]/.test(contentType) ||
        contentType.split(";")[0]!.trim().toLowerCase() !== "application/json"))
      throw new UnsupportedMediaTypeException({ code: "IDEMPOTENCY_JSON_REQUIRED" });

    let bodyJson: string | null = null;
    if (req.rawBody !== undefined) {
      if (!Buffer.isBuffer(req.rawBody))
        throw new ServiceUnavailableException({ code: "IDEMPOTENCY_RAW_BODY_CONFIGURATION" });
      if (req.rawBody.byteLength > MAX_REQUEST_BYTES)
        throw new HttpException({ code: "IDEMPOTENCY_BODY_TOO_LARGE" },
          HttpStatus.PAYLOAD_TOO_LARGE);
      try {
        bodyJson = req.rawBody.byteLength ? STRICT_UTF8.decode(req.rawBody) : null;
      } catch {
        throw new BadRequestException({ code: "IDEMPOTENCY_INVALID_UTF8" });
      }
    } else if (req.body !== undefined || req.headers["content-length"] !== undefined &&
        req.headers["content-length"] !== "0") {
      // NestFactory.create(AppModule, {rawBody: true}) is mandatory for JSON writes.
      throw new ServiceUnavailableException({ code: "IDEMPOTENCY_RAW_BODY_CONFIGURATION" });
    }

    if (bodyJson !== null && !contentType)
      throw new UnsupportedMediaTypeException({ code: "IDEMPOTENCY_JSON_REQUIRED" });

    let scopeKey: string;
    let fingerprint: string;
    try {
      const caller = this.options.resolveCaller(req);
      if (!caller?.principalId?.trim()) throw new Error("No authenticated principal.");
      const parsed = new URL(req.originalUrl ?? req.url ?? "/", "http://local.invalid");
      scopeKey = computeScopeKey({
        namespace: this.options.namespace,
        operationId: route.operationId,
        principalId: caller.principalId,
        tenantId: caller.tenantId,
        clientKey: key,
      });
      fingerprint = computeRequestFingerprint({
        method: req.method,
        operationId: route.operationId,
        path: parsed.pathname,
        query: [...parsed.searchParams.entries()].map(([name, value]) => ({ name, value })),
        bodyJson,
      });
    } catch (error) {
      if (error instanceof Error && /principal/i.test(error.message))
        throw new ServiceUnavailableException({ code: "IDEMPOTENCY_SCOPE_CONFIGURATION" });
      throw new BadRequestException({ code: "IDEMPOTENCY_INVALID_REQUEST" });
    }

    let decision;
    try {
      decision = await this.options.store.tryClaim(
        scopeKey, fingerprint, this.options.leaseMs ?? 30_000);
    } catch (error) {
      this.logger.error("PostgreSQL idempotency claim failed.");
      throw new ServiceUnavailableException({ code: "IDEMPOTENCY_STORAGE_UNAVAILABLE" });
    }

    switch (decision.status) {
      case "in_progress":
        res.setHeader("Retry-After", "1");
        throw new ConflictException({ code: "IDEMPOTENCY_IN_PROGRESS" });
      case "fingerprint_mismatch":
        throw new HttpException({ code: "IDEMPOTENCY_FINGERPRINT_MISMATCH" },
          HttpStatus.UNPROCESSABLE_ENTITY);
      case "uncertain":
        throw new ConflictException({ code: "IDEMPOTENCY_OUTCOME_UNCERTAIN" });
      case "completed": {
        if (!decision.response)
          throw new ServiceUnavailableException({ code: "IDEMPOTENCY_RESULT_MISSING" });
        res.statusCode = decision.response.statusCode;
        res.setHeader("Content-Type", decision.response.contentType);
        for (const [name, value] of Object.entries(decision.response.headers))
          if (SAFE_HEADERS.includes(name.toLowerCase() as typeof SAFE_HEADERS[number]))
            res.setHeader(name, value);
        try {
          // NestJS owns the response; returning a JSON value avoids manually
          // ending the response behind Nest's router response controller.
          return JSON.parse(STRICT_UTF8.decode(decision.response.body)) as unknown;
        } catch {
          throw new ServiceUnavailableException({ code: "IDEMPOTENCY_STORED_RESPONSE_INVALID" });
        }
      }
      case "acquired":
        if (!decision.ownerToken)
          throw new ServiceUnavailableException({ code: "IDEMPOTENCY_OWNER_MISSING" });
        return this.executeAndPersist(next, res, scopeKey, fingerprint, decision.ownerToken);
      default:
        throw new ServiceUnavailableException({ code: "IDEMPOTENCY_INVALID_STATE" });
    }
  }

  private async executeAndPersist(
    next: CallHandler, res: HttpResponse,
    scopeKey: string, fingerprint: string, ownerToken: string,
  ): Promise<unknown> {
    try {
      const value: unknown = await lastValueFrom(next.handle());
      if (res.headersSent || res.statusCode < 200 || res.statusCode > 299)
        throw new Error("Unsupported status or manual response.");

      // The first release supports ordinary JSON DTOs, NOT streams, custom
      // class serialization, Express @Res() writes or other response modifiers.
      const json = JSON.stringify(value);
      if (typeof json !== "string")
        throw new Error("Handler must return a JSON-serializable value.");

      const body = Buffer.from(json, "utf8");
      if (body.length > MAX_RESPONSE_BYTES)
        throw new Error("JSON response exceeds 256 KiB.");

      const declaredContentType = res.getHeader("content-type");
      const contentType = typeof declaredContentType === "string"
        ? declaredContentType : "application/json; charset=utf-8";
      if (contentType.length > 128 || /[\r\n]/.test(contentType) ||
          contentType.split(";")[0]!.trim().toLowerCase() !== "application/json")
        throw new Error("Only application/json responses are supported.");

      const headers: Record<string, string> = {};
      for (const name of SAFE_HEADERS) {
        const header = res.getHeader(name);
        if (header !== undefined) {
          if (typeof header !== "string" || header.length > 4096 || /[\r\n]/.test(header))
            throw new Error("Unsafe replayable response header.");
          headers[name] = header;
        }
      }

      const response: StoredHttpResponse = {
        statusCode: res.statusCode, contentType, body, headers,
      };
      const saved = await this.options.store.tryComplete(
        scopeKey, fingerprint, ownerToken, response,
        this.options.retentionMs ?? 86_400_000);
      if (!saved) throw new Error("Idempotency completion/owner lease failed.");
      return value;
    } catch (error) {
      // A handler can commit business data and fail after it. Never reexecute
      // on a new request just because response persistence was unsuccessful.
      try {
        await this.options.store.tryMarkUncertain(scopeKey, fingerprint, ownerToken);
      } catch {
        this.logger.error("Could not mark an idempotency operation uncertain.");
      }
      this.logger.error("Protected handler did not produce a durable replayable result.");
      throw new ServiceUnavailableException({ code: "IDEMPOTENCY_EXECUTION_UNCERTAIN" });
    }
  }
}
