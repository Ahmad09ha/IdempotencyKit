import type { StoredHttpResponse } from "./contracts.js";

/**
 * The storage contract coordinates Standard-mode requests. It does not
 * make an unrelated business transaction atomic or protect external effects.
 */
export type IdempotencyClaimStatus =
  | "acquired" | "in_progress" | "completed" | "fingerprint_mismatch" | "uncertain";

export interface IdempotencyClaimResult {
  readonly status: IdempotencyClaimStatus;
  /** Present only when status is acquired. */
  readonly ownerToken?: string;
  /** Present only when status is completed. */
  readonly response?: StoredHttpResponse;
}

export interface IdempotencyStore {
  tryClaim(
    scopeKeyHash: string,
    requestFingerprint: string,
    leaseMs: number,
  ): Promise<IdempotencyClaimResult>;

  tryComplete(
    scopeKeyHash: string,
    requestFingerprint: string,
    ownerToken: string,
    response: StoredHttpResponse,
    retentionMs: number,
  ): Promise<boolean>;

  tryExtendLease(
    scopeKeyHash: string,
    ownerToken: string,
    leaseMs: number,
  ): Promise<boolean>;

  /** Mark an owned processing request uncertain after a handler or capture failure. */
  tryMarkUncertain(
    scopeKeyHash: string,
    requestFingerprint: string,
    ownerToken: string,
  ): Promise<boolean>;

  purgeExpiredCompleted(batchSize: number): Promise<number>;
}
