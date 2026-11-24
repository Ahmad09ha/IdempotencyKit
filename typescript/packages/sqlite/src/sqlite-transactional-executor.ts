import { randomUUID } from "node:crypto";
import type {
  StoredHttpResponse, TransactionalIdempotencyExecutor,
  TransactionalIdempotencyResult,
} from "@idempotencykit/core";
import {
  SQLITE_NOW_MS, MAX_LEASE_MS, MAX_RETENTION_MS, READ_SQL,
  makeSession, openSqliteDatabase, prepareResponse, readResponse,
  sqlitePath, validateBusyTimeout, validateDuration, validateHash,
  withLocalFileGate,
} from "./sqlite-primitives.js";
import type { SqliteRow, SqliteTransactionSession } from "./sqlite-primitives.js";

/**
 * One LOCAL on-disk SQLite database; BEGIN IMMEDIATE reserves the sole writer
 * before the business callback. The built-in driver is synchronous, but the
 * async callback is supported safely through a per-file in-process queue.
 * Keep the callback SHORT: awaiting external I/O while holding a write lock
 * is poor practice and external effects are not rolled back.
 *
 * SQLITE_BUSY (including a different key) propagates as storage failure.
 * This adapter never converts an unproven busy writer to same-key InProgress.
 */
export class SqliteTransactionalExecutor
  implements TransactionalIdempotencyExecutor<SqliteTransactionSession> {
  private readonly file: string;

  constructor(databasePath: string, private readonly busyTimeoutMs = 250) {
    this.file = sqlitePath(databasePath);
    validateBusyTimeout(busyTimeoutMs);
  }

  async execute(
    scopeKeyHash: string,
    requestFingerprint: string,
    operation: (session: SqliteTransactionSession) => Promise<StoredHttpResponse>,
    retentionMs: number,
  ): Promise<TransactionalIdempotencyResult> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateDuration(retentionMs, MAX_RETENTION_MS);
    if (typeof operation !== "function")
      throw new Error("Business callback is required.");
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        db.exec("BEGIN IMMEDIATE");
        const row = db.prepare(READ_SQL).get(scopeKeyHash) as
          unknown as SqliteRow | undefined;
        if (row) {
          let existing: TransactionalIdempotencyResult;
          if (row.request_fingerprint !== requestFingerprint)
            existing = { status: "fingerprint_mismatch" };
          else if (row.state === "completed")
            existing = { status: "replayed", response: readResponse(row) };
          else if (row.state === "uncertain")
            existing = { status: "uncertain" };
          else if (row.state === "processing") {
            if (row.expired || row.lease_expires_at === null) {
              db.prepare(
                "UPDATE idempotencykit_records SET state='uncertain'," +
                "owner_token=NULL,lease_expires_at=NULL " +
                "WHERE scope_key_hash=? AND state='processing'",
              ).run(scopeKeyHash);
              existing = { status: "uncertain" };
            } else {
              existing = { status: "in_progress" };
            }
          } else
            throw new Error("Unknown SQLite record state.");

          db.exec("COMMIT");
          return existing;
        }

        const owner = randomUUID();
        db.prepare(
          "INSERT INTO idempotencykit_records " +
          "(scope_key_hash,request_fingerprint,state,owner_token,lease_expires_at) " +
          "VALUES (?,?,'processing',?," + SQLITE_NOW_MS + "+?)",
        ).run(scopeKeyHash, requestFingerprint, owner, MAX_LEASE_MS);

        const response = await operation(makeSession(db));
        if (!db.isTransaction)
          throw new Error("Business callback closed the SQLite transaction.");
        const prepared = prepareResponse(response);
        const completed = db.prepare(
          "UPDATE idempotencykit_records SET state='completed',owner_token=NULL," +
          "lease_expires_at=NULL,response_status=?,response_content_type=?," +
          "response_body=?,response_headers=?,completed_at=" + SQLITE_NOW_MS +
          ",expires_at=" + SQLITE_NOW_MS + "+? " +
          "WHERE scope_key_hash=? AND request_fingerprint=? AND " +
          "state='processing' AND owner_token=?",
        ).run(prepared.response.statusCode, prepared.response.contentType,
          Buffer.from(prepared.response.body), prepared.headersJson,
          retentionMs, scopeKeyHash, requestFingerprint, owner).changes;
        if (completed !== 1)
          throw new Error("SQLite transaction lost its idempotency claim.");

        db.exec("COMMIT");
        return { status: "executed", response: prepared.response };
      } catch (error) {
        if (db.isTransaction) {
          try { db.exec("ROLLBACK"); }
          catch { /* SQLite can abort a transaction during SQLITE_FULL/I/O failure. */ }
        }
        // Never repeat an ambiguous COMMIT automatically.
        throw error;
      } finally {
        db.close();
      }
    });
  }
}
