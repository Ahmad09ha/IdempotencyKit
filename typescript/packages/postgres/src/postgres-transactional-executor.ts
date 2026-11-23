import { randomUUID } from "node:crypto";
import type { Pool, PoolClient, QueryResultRow } from "pg";
import type { StoredHttpResponse } from "@idempotencykit/core";
import { readResponse, validateResponse } from "./postgres-idempotency-store.js";

const HASH = /^v1:[0-9a-f]{64}$/;
const MAX_RETENTION_MS = 365 * 24 * 60 * 60 * 1000;

export type TransactionalIdempotencyStatus =
  | "executed" | "replayed" | "in_progress" | "fingerprint_mismatch" | "uncertain";

export interface TransactionalIdempotencyResult {
  readonly status: TransactionalIdempotencyStatus;
  /** Only executed and replayed outcomes carry a response. */
  readonly response?: StoredHttpResponse;
}

type ExistingRow = QueryResultRow & {
  request_fingerprint: string;
  state: "processing" | "completed" | "uncertain";
  lease_expired: boolean | null;
  response_status: number | null;
  response_content_type: string | null;
  response_body: Buffer | null;
  response_headers: string | null;
};

function assertHash(value: string): void {
  if (typeof value !== "string" || !HASH.test(value))
    throw new Error("Expected a v1: SHA-256 scope or fingerprint hash.");
}

function assertMilliseconds(value: number, minimum: number, maximum: number): void {
  if (!Number.isSafeInteger(value) || value < minimum || value > maximum)
    throw new RangeError("Duration must be an integer millisecond count within supported bounds.");
}

/**
 * Explicit same-PostgreSQL-transaction execution API.
 *
 * The caller's SQL MUST use the supplied PoolClient. Do NOT use pool.query()
 * or another connection inside the callback, and do not assume external
 * service calls or emails can be rolled back with the database transaction.
 *
 * The transaction first reserves a unique idempotency key (uncommitted),
 * executes the business SQL, validates the replay response, records completion,
 * and commits everything together. Competing claims block on the unique key
 * until the transaction commits/rolls back, or get in_progress on lock timeout.
 */
export class PostgresTransactionalExecutor {
  constructor(
    private readonly pool: Pool,
    private readonly claimLockTimeoutMs: number = 2_000,
  ) {
    if (!pool) throw new TypeError("A PostgreSQL pool is required.");
    assertMilliseconds(claimLockTimeoutMs, 1, 30_000);
  }

  async execute(
    scopeKeyHash: string,
    requestFingerprint: string,
    operation: (client: PoolClient) => Promise<StoredHttpResponse>,
    retentionMs: number,
  ): Promise<TransactionalIdempotencyResult> {
    assertHash(scopeKeyHash);
    assertHash(requestFingerprint);
    if (typeof operation !== "function") throw new TypeError("A transaction callback is required.");
    assertMilliseconds(retentionMs, 1, MAX_RETENTION_MS);

    const client = await this.pool.connect();
    let openTransaction = false;
    try {
      await client.query("BEGIN ISOLATION LEVEL READ COMMITTED");
      openTransaction = true;
      await client.query("SELECT set_config('lock_timeout', $1, true)", [
        this.claimLockTimeoutMs.toString() + "ms",
      ]);

      const owner = randomUUID();
      let inserted;
      try {
        inserted = await client.query(
          `INSERT INTO idempotencykit_records (
             scope_key_hash, request_fingerprint, state, owner_token, lease_expires_at)
           VALUES ($1, $2, 'processing', $3::uuid, clock_timestamp() + interval '1 day')
           ON CONFLICT (scope_key_hash) DO NOTHING
           RETURNING owner_token`,
          [scopeKeyHash, requestFingerprint, owner],
        );
      } catch (error) {
        if ((error as { code?: unknown })?.code === "55P03") {
          await client.query("ROLLBACK");
          openTransaction = false;
          return { status: "in_progress" };
        }
        throw error;
      }

      if (inserted.rowCount !== 1) {
        const inspection = await client.query<ExistingRow>(
          `SELECT request_fingerprint, state,
                  lease_expires_at <= clock_timestamp() AS lease_expired,
                  response_status, response_content_type,
                  response_body, response_headers::text
             FROM idempotencykit_records WHERE scope_key_hash = $1`,
          [scopeKeyHash],
        );
        const record = inspection.rows[0];
        if (!record)
          throw new Error("Conflicting idempotency record disappeared; retry later.");

        let outcome: TransactionalIdempotencyResult;
        if (record.request_fingerprint !== requestFingerprint)
          outcome = { status: "fingerprint_mismatch" };
        else if (record.state === "processing")
          outcome = { status: record.lease_expired ? "uncertain" : "in_progress" };
        else if (record.state === "uncertain")
          outcome = { status: "uncertain" };
        else if (record.state === "completed")
          outcome = { status: "replayed", response: readResponse(record) };
        else
          throw new Error("Unexpected idempotency record state.");

        await client.query("ROLLBACK");
        openTransaction = false;
        return outcome;
      }

      // The business callback is the ONLY place domain writes should happen.
      // All of them must use this exact client/transaction.
      const response = await operation(client);
      const safeHeaders = validateResponse(response);
      // Verify the body is real single-value UTF-8 JSON *before* committing.
      const rawBody = new TextDecoder("utf-8", { fatal: true }).decode(response.body);
      JSON.parse(rawBody);
      const headersJson = JSON.stringify(safeHeaders);

      const completed = await client.query(
        `UPDATE idempotencykit_records
            SET state = 'completed', owner_token = NULL, lease_expires_at = NULL,
                response_status = $4, response_content_type = $5,
                response_body = $6, response_headers = $7::jsonb,
                completed_at = clock_timestamp(),
                expires_at = clock_timestamp()
                    + ($8::double precision * interval '1 millisecond')
          WHERE scope_key_hash = $1 AND request_fingerprint = $2
            AND state = 'processing' AND owner_token = $3::uuid`,
        [scopeKeyHash, requestFingerprint, owner,
         response.statusCode, response.contentType,
         Buffer.from(response.body), headersJson, retentionMs],
      );
      if (completed.rowCount !== 1)
        throw new Error("Transactional completion lost the processing claim.");

      // One atomic commit: all business SQL and the completed replay response.
      await client.query("COMMIT");
      openTransaction = false;
      return { status: "executed", response: {
        ...response, headers: safeHeaders,
      } };
    } catch (error) {
      if (openTransaction) {
        try { await client.query("ROLLBACK"); } catch { /* Connection may already be broken. */ }
      }
      // Do not automatically retry a failed/ambiguous commit: the caller must
      // resolve the same scoped key against the authoritative primary first.
      throw error;
    } finally {
      client.release();
    }
  }
}
