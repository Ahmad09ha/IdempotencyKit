import { Inject, Injectable, Logger } from "@nestjs/common";
import type { IdempotencyHttpRequest } from "@idempotencykit/nestjs";
import type { StoredHttpResponse } from "@idempotencykit/core";
import { computeRequestFingerprint, computeScopeKey } from "@idempotencykit/core";
import type { PoolClient } from "pg";
import {
  POSTGRES_TRANSACTIONAL_HTTP_OPTIONS,
} from "./postgres-transactional-http-options.js";
import type {
  PostgresTransactionalHttpOptions, TransactionalExpressResponse,
} from "./postgres-transactional-http-options.js";

const MAX_REQUEST_BYTES = 65_536;
const SAFE_HEADERS = new Set(["location", "etag", "content-language"]);
const STRICT_UTF8 = new TextDecoder("utf-8", { fatal: true });
const VALID_KEY = /^[A-Za-z0-9._:-]{1,255}$/;

class RequestProblem extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) { super(message); }
}

/**
 * Express-only, explicit Transactional-mode HTTP operation. Use @Req() and
 * @Res() on a route guarded by a REAL authentication guard.
 *
 * NEVER apply Standard-mode @Idempotent() on this route. The callback receives
 * the same PostgreSQL PoolClient that writes the completed idempotency record.
 */
@Injectable()
export class PostgresTransactionalHttpService {
  private readonly logger = new Logger(PostgresTransactionalHttpService.name);

  constructor(
    @Inject(POSTGRES_TRANSACTIONAL_HTTP_OPTIONS)
    private readonly options: PostgresTransactionalHttpOptions,
  ) {}

  async handleExpress(
    request: IdempotencyHttpRequest,
    response: TransactionalExpressResponse,
    operationId: string,
    operation: (
      client: PoolClient,
      bodyJson: string | null,
      request: IdempotencyHttpRequest,
    ) => Promise<StoredHttpResponse>,
    /** Optional fresh, domain-level authorization check, before any replay. */
    authorizeOperation?: (request: IdempotencyHttpRequest) => Promise<boolean> | boolean,
  ): Promise<void> {
    if (!request || !response || typeof response.end !== "function" ||
        typeof response.setHeader !== "function")
      throw new Error("NestJS Express @Req() and @Res() are required.");
    if (response.headersSent)
      throw new Error("Response was already started; transactional capture impossible.");
    if (typeof operation !== "function")
      throw new TypeError("A transactional business callback is required.");

    let scopeKey: string;
    let fingerprint: string;
    let bodyJson: string | null;

    try {
      if (!request.user)
        throw new RequestProblem(401, "IDEMPOTENCY_AUTH_REQUIRED",
          "An authenticated guard must populate req.user.");
      if (!["POST", "PUT", "PATCH"].includes(request.method?.toUpperCase()))
        throw new RequestProblem(405, "IDEMPOTENCY_METHOD_UNSUPPORTED",
          "Only POST, PUT and PATCH are supported.");
      if (typeof operationId !== "string" || !operationId.trim())
        throw new RequestProblem(503, "IDEMPOTENCY_OPERATION_CONFIG",
          "A stable operation ID is required.");

      const key = request.headers["idempotency-key"];
      const raw = request.rawHeaders ?? [];
      const occurrence = raw.filter((value, index) =>
        index % 2 === 0 && value.toLowerCase() === "idempotency-key").length;
      if (typeof key !== "string" || !VALID_KEY.test(key) ||
          (raw.length > 0 && occurrence !== 1))
        throw new RequestProblem(400, "IDEMPOTENCY_INVALID_REQUEST",
          "Exactly one valid Idempotency-Key header is required.");

      const encoding = request.headers["content-encoding"];
      if (encoding && encoding !== "identity")
        throw new RequestProblem(415, "IDEMPOTENCY_CONTENT_ENCODING_UNSUPPORTED",
          "Content encoding is not supported.");

      const contentType = request.headers["content-type"];
      if (contentType !== undefined &&
          (typeof contentType !== "string" || contentType.length > 128 ||
          /[\r\n]/.test(contentType) ||
          contentType.split(";")[0]!.trim().toLowerCase() !== "application/json"))
        throw new RequestProblem(415, "IDEMPOTENCY_JSON_REQUIRED",
          "Only application/json is supported.");

      if (request.rawBody !== undefined) {
        if (!Buffer.isBuffer(request.rawBody))
          throw new RequestProblem(503, "IDEMPOTENCY_RAW_BODY_CONFIGURATION",
            "NestJS rawBody must be a Buffer.");
        if (request.rawBody.byteLength > MAX_REQUEST_BYTES)
          throw new RequestProblem(413, "IDEMPOTENCY_BODY_TOO_LARGE",
            "Request body exceeds 64 KiB.");
        try {
          bodyJson = request.rawBody.byteLength
            ? STRICT_UTF8.decode(request.rawBody) : null;
        } catch {
          throw new RequestProblem(400, "IDEMPOTENCY_INVALID_REQUEST",
            "Request body is not valid UTF-8.");
        }
      } else if (request.body !== undefined ||
          (request.headers["content-length"] !== undefined &&
            request.headers["content-length"] !== "0")) {
        throw new RequestProblem(503, "IDEMPOTENCY_RAW_BODY_CONFIGURATION",
          "Enable NestJS rawBody: true before handling transactional JSON.");
      } else {
        bodyJson = null;
      }

      if (bodyJson !== null && contentType === undefined)
        throw new RequestProblem(415, "IDEMPOTENCY_JSON_REQUIRED",
          "Content type is required when a body exists.");

      let caller;
      try {
        caller = this.options.resolveCaller(request);
        if (!caller?.principalId?.trim())
          throw new Error("Missing trusted principal.");
      } catch {
        throw new RequestProblem(503, "IDEMPOTENCY_SCOPE_CONFIGURATION",
          "Could not resolve an authenticated caller.");
      }

      try {
        const parsed = new URL(request.originalUrl ?? request.url ?? "/",
          "http://idempotency.invalid");
        scopeKey = computeScopeKey({
          namespace: this.options.namespace,
          operationId,
          principalId: caller.principalId,
          tenantId: caller.tenantId,
          clientKey: key,
        });
        fingerprint = computeRequestFingerprint({
          method: request.method,
          operationId,
          path: parsed.pathname,
          query: [...parsed.searchParams.entries()].map(([name, value]) =>
            ({ name, value })),
          bodyJson,
        });
      } catch {
        throw new RequestProblem(400, "IDEMPOTENCY_INVALID_REQUEST",
          "Invalid scoped key or JSON request.");
      }
    } catch (error) {
      if (error instanceof RequestProblem) {
        this.writeProblem(response, error.status, error.code, error.message);
        return;
      }
      throw error;
    }

    if (authorizeOperation) {
      let allowed: boolean;
      try { allowed = await authorizeOperation(request); }
      catch {
        this.writeProblem(response, 503, "IDEMPOTENCY_AUTHORIZATION_UNAVAILABLE",
          "Could not check operation permissions.");
        return;
      }
      if (!allowed) {
        this.writeProblem(response, 403, "IDEMPOTENCY_OPERATION_FORBIDDEN",
          "Operation is not authorized.");
        return;
      }
    }

    try {
      const result = await this.options.executor.execute(
        scopeKey, fingerprint,
        client => operation(client, bodyJson, request),
        this.options.retentionMs ?? 86_400_000,
      );
      switch (result.status) {
        case "executed":
        case "replayed":
          if (!result.response) {
            this.writeProblem(response, 503, "IDEMPOTENCY_MISSING_RESULT",
              "Completed response is unavailable.");
            return;
          }
          this.writeResponse(response, result.response);
          return;
        case "in_progress":
          response.setHeader("Retry-After", "1");
          this.writeProblem(response, 409, "IDEMPOTENCY_IN_PROGRESS",
            "A matching operation is processing.");
          return;
        case "fingerprint_mismatch":
          this.writeProblem(response, 422, "IDEMPOTENCY_FINGERPRINT_MISMATCH",
            "Key reused with different request data.");
          return;
        case "uncertain":
          this.writeProblem(response, 409, "IDEMPOTENCY_OUTCOME_UNCERTAIN",
            "Previous outcome requires reconciliation.");
          return;
        default:
          throw new Error("Unknown transaction result.");
      }
    } catch (error) {
      this.logger.error("Transactional operation failed or COMMIT is ambiguous.");
      // Do NOT execute callback again; same-key retry resolves committed state.
      this.writeProblem(response, 503, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE",
        "Transaction failed or its outcome is not confirmed. Retry the same key.");
    }
  }

  private writeResponse(
    response: TransactionalExpressResponse, stored: StoredHttpResponse,
  ): void {
    response.statusCode = stored.statusCode;
    response.setHeader("Content-Type", stored.contentType);
    for (const [name, value] of Object.entries(stored.headers))
      if (SAFE_HEADERS.has(name.toLowerCase()))
        response.setHeader(name, value);
    response.setHeader("Content-Length", stored.body.byteLength);
    response.end(Buffer.from(stored.body));
  }

  private writeProblem(
    response: TransactionalExpressResponse,
    status: number, code: string, detail: string,
  ): void {
    const bytes = Buffer.from(JSON.stringify({
      type: "about:blank", title: code, status, code, detail,
    }), "utf8");
    response.statusCode = status;
    response.setHeader("Content-Type", "application/problem+json");
    response.setHeader("Content-Length", bytes.length);
    response.end(bytes);
  }
}
