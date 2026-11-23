import { DynamicModule, Module } from "@nestjs/common";
import { APP_INTERCEPTOR } from "@nestjs/core";
import type { IdempotencyStore } from "@idempotencykit/core";
import { IdempotencyInterceptor } from "./idempotency.interceptor.js";

export interface IdempotencyCallerScope {
  readonly principalId: string;
  readonly tenantId?: string | null;
}

/** Server-controlled request information; never derive caller identity from untrusted headers. */
export interface IdempotencyHttpRequest {
  readonly method: string;
  readonly url?: string;
  readonly originalUrl?: string;
  readonly rawBody?: Buffer;
  readonly body?: unknown;
  readonly user?: unknown;
  readonly headers: Readonly<Record<string, string | string[] | undefined>>;
  readonly rawHeaders?: readonly string[];
}

export interface IdempotencyKitOptions {
  /** Stable service namespace, e.g. orders-api. */
  readonly namespace: string;
  readonly store: IdempotencyStore;
  /** Must use authenticated guard/user context; no caller-controlled header fallback. */
  readonly resolveCaller: (request: IdempotencyHttpRequest) => IdempotencyCallerScope;
  readonly leaseMs?: number;       // default 30 seconds
  readonly retentionMs?: number;   // default 24 hours
}

export const IDEMPOTENCY_HTTP_OPTIONS = Symbol("IDEMPOTENCY_HTTP_OPTIONS");

/** Provides a global interceptor that acts only on @Idempotent endpoints. */
@Module({})
export class IdempotencyKitModule {
  static forRoot(options: IdempotencyKitOptions): DynamicModule {
    if (!options || !options.namespace?.trim() || !options.store ||
        typeof options.resolveCaller !== "function")
      throw new Error("namespace, store and resolveCaller must be configured.");

    const lease = options.leaseMs ?? 30_000;
    const retention = options.retentionMs ?? 86_400_000;
    if (!Number.isSafeInteger(lease) || lease < 1 || lease > 86_400_000 ||
        !Number.isSafeInteger(retention) || retention < 1 || retention > 31_536_000_000)
      throw new Error("Lease or retention is outside the supported millisecond range.");

    return {
      module: IdempotencyKitModule,
      providers: [
        { provide: IDEMPOTENCY_HTTP_OPTIONS, useValue: options },
        { provide: APP_INTERCEPTOR, useClass: IdempotencyInterceptor },
      ],
      exports: [IDEMPOTENCY_HTTP_OPTIONS],
    };
  }
}
