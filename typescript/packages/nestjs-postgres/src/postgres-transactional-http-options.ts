import type { IdempotencyHttpRequest, IdempotencyCallerScope } from "@idempotencykit/nestjs";
import type { PostgresTransactionalExecutor } from "@idempotencykit/postgres";

export interface PostgresTransactionalHttpOptions {
  readonly namespace: string;
  readonly executor: PostgresTransactionalExecutor;
  /** Resolve identity from a verified NestJS guard, never from request headers. */
  readonly resolveCaller: (request: IdempotencyHttpRequest) => IdempotencyCallerScope;
  readonly retentionMs?: number; // 24 hours by default
}

/** Express response methods required by this explicit transactional adapter. */
export interface TransactionalExpressResponse {
  statusCode: number;
  headersSent?: boolean;
  setHeader(name: string, value: string | number): void;
  end(chunk?: Uint8Array): void;
}

export const POSTGRES_TRANSACTIONAL_HTTP_OPTIONS = Symbol(
  "IDEMPOTENCYKIT_POSTGRES_TRANSACTIONAL_HTTP_OPTIONS",
);
