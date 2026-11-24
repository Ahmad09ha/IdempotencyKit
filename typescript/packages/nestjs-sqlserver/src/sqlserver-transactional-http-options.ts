import type { IdempotencyHttpRequest, IdempotencyCallerScope } from "@idempotencykit/nestjs";
import type { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";

export interface SqlServerTransactionalHttpOptions {
  readonly namespace: string;
  readonly executor: SqlServerTransactionalExecutor;
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

export const SQLSERVER_TRANSACTIONAL_HTTP_OPTIONS = Symbol(
  "IDEMPOTENCYKIT_SQLSERVER_TRANSACTIONAL_HTTP_OPTIONS",
);
