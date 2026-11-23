import type { IdempotencyStore } from "@idempotencykit/core";

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
