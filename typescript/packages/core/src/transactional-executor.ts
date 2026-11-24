import type { StoredHttpResponse } from "./contracts.js";

/**
 * Canonical cross-language outcome names for transactional idempotency.
 * A provider must not silently map storage errors or uncertain business
 * effects to "executed" or "replayed".
 */
export const TransactionalExecutionStatuses = Object.freeze({
  Executed: "executed",
  Replayed: "replayed",
  InProgress: "in_progress",
  FingerprintMismatch: "fingerprint_mismatch",
  Uncertain: "uncertain",
} as const);

export type TransactionalIdempotencyStatus =
  (typeof TransactionalExecutionStatuses)[keyof typeof TransactionalExecutionStatuses];

export interface TransactionalIdempotencyResult {
  readonly status: TransactionalIdempotencyStatus;
  /** Only executed and replayed statuses contain a persisted response. */
  readonly response?: StoredHttpResponse;
}

/**
 * Provider-neutral contract, typed on the backend's actual transactional
 * session. The callback MUST perform all domain SQL using that session.
 *
 * A PostgreSQL implementation passes pg.PoolClient. Future SQL Server and
 * SQLite adapters may pass different objects with different query APIs.
 * The executor owns COMMIT/ROLLBACK and never reruns callbacks automatically
 * when a COMMIT acknowledgement is ambiguous.
 */
export interface TransactionalIdempotencyExecutor<TSession> {
  execute(
    scopeKeyHash: string,
    requestFingerprint: string,
    operation: (session: TSession) => Promise<StoredHttpResponse>,
    retentionMs: number,
  ): Promise<TransactionalIdempotencyResult>;
}
