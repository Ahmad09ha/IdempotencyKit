import { randomUUID } from "node:crypto";
import type { IdempotencyClaimResult, IdempotencyStore, StoredHttpResponse } from
  "@idempotencykit/core";
import {
  SQLITE_NOW_MS, MAX_LEASE_MS, MAX_RETENTION_MS, READ_SQL,
  openSqliteDatabase, prepareResponse, readResponse, sqlitePath,
  validateBusyTimeout, validateDuration, validateHash, validateOwner,
  withLocalFileGate,
} from "./sqlite-primitives.js";
import type { SqliteRow } from "./sqlite-primitives.js";

/**
 * Durable Standard mode for one LOCAL SQLite database file. This adapter
 * serializes its own async operations in-process; SQLITE_BUSY from another
 * process or unrelated writer is a STORAGE ERROR, never same-key InProgress.
 */
export class SqliteIdempotencyStore implements IdempotencyStore {
  private readonly file: string;
  constructor(databasePath: string, private readonly busyTimeoutMs = 250) {
    this.file = sqlitePath(databasePath);
    validateBusyTimeout(busyTimeoutMs);
  }

  async tryClaim(
    scopeKeyHash: string, requestFingerprint: string, leaseMs: number,
  ): Promise<IdempotencyClaimResult> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateDuration(leaseMs, MAX_LEASE_MS);
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        db.exec("BEGIN IMMEDIATE");
        const row = db.prepare(READ_SQL).get(scopeKeyHash) as
          unknown as SqliteRow | undefined;
        let result: IdempotencyClaimResult;
        if (!row) {
          const ownerToken = randomUUID();
          db.prepare(
            "INSERT INTO idempotencykit_records " +
            "(scope_key_hash,request_fingerprint,state,owner_token,lease_expires_at) " +
            "VALUES (?,?,'processing',?," + SQLITE_NOW_MS + "+?)",
          ).run(scopeKeyHash, requestFingerprint, ownerToken, leaseMs);
          result = { status: "acquired", ownerToken };
        } else if (row.request_fingerprint !== requestFingerprint)
          result = { status: "fingerprint_mismatch" };
        else if (row.state === "processing") {
          const changed = db.prepare(
            "UPDATE idempotencykit_records SET state='uncertain', " +
            "owner_token=NULL, lease_expires_at=NULL " +
            "WHERE scope_key_hash=? AND state='processing' AND " +
            "(lease_expires_at IS NULL OR lease_expires_at <= " + SQLITE_NOW_MS + ")",
          ).run(scopeKeyHash).changes;
          result = { status: changed === 1 ? "uncertain" : "in_progress" };
        } else if (row.state === "uncertain")
          result = { status: "uncertain" };
        else if (row.state === "completed")
          result = { status: "completed", response: readResponse(row) };
        else
          throw new Error("Unknown SQLite record state.");
        db.exec("COMMIT");
        return result;
      } catch (error) {
        if (db.isTransaction) {
          try { db.exec("ROLLBACK"); } catch { /* I/O may have closed the database. */ }
        }
        throw error;
      } finally {
        db.close();
      }
    });
  }

  async tryComplete(
    scopeKeyHash: string, requestFingerprint: string, ownerToken: string,
    response: StoredHttpResponse, retentionMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateOwner(ownerToken);
    validateDuration(retentionMs, MAX_RETENTION_MS);
    const prepared = prepareResponse(response);
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        const changes = db.prepare(
          "UPDATE idempotencykit_records SET state='completed',owner_token=NULL," +
          "lease_expires_at=NULL,response_status=?,response_content_type=?," +
          "response_body=?,response_headers=?,completed_at=" + SQLITE_NOW_MS +
          ",expires_at=" + SQLITE_NOW_MS + "+? " +
          "WHERE scope_key_hash=? AND request_fingerprint=? AND " +
          "state='processing' AND owner_token=? AND lease_expires_at > " +
          SQLITE_NOW_MS,
        ).run(prepared.response.statusCode, prepared.response.contentType,
          Buffer.from(prepared.response.body), prepared.headersJson,
          retentionMs, scopeKeyHash, requestFingerprint, ownerToken).changes;
        return changes === 1;
      } finally { db.close(); }
    });
  }

  async tryExtendLease(
    scopeKeyHash: string, ownerToken: string, leaseMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateOwner(ownerToken);
    validateDuration(leaseMs, MAX_LEASE_MS);
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        const changes = db.prepare(
          "UPDATE idempotencykit_records SET lease_expires_at=" +
          SQLITE_NOW_MS + "+? WHERE scope_key_hash=? AND state='processing' " +
          "AND owner_token=? AND lease_expires_at > " + SQLITE_NOW_MS,
        ).run(leaseMs, scopeKeyHash, ownerToken).changes;
        return changes === 1;
      } finally { db.close(); }
    });
  }

  async tryMarkUncertain(
    scopeKeyHash: string, requestFingerprint: string, ownerToken: string,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateOwner(ownerToken);
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        const changes = db.prepare(
          "UPDATE idempotencykit_records SET state='uncertain',owner_token=NULL," +
          "lease_expires_at=NULL WHERE scope_key_hash=? AND " +
          "request_fingerprint=? AND state='processing' AND owner_token=?",
        ).run(scopeKeyHash, requestFingerprint, ownerToken).changes;
        return changes === 1;
      } finally { db.close(); }
    });
  }

  async purgeExpiredCompleted(batchSize: number): Promise<number> {
    if (!Number.isSafeInteger(batchSize) || batchSize < 1 || batchSize > 1_000)
      throw new RangeError("Batch must be from 1 to 1000.");
    return withLocalFileGate(this.file, async () => {
      const db = openSqliteDatabase(this.file, this.busyTimeoutMs);
      try {
        return Number(db.prepare(
          "DELETE FROM idempotencykit_records WHERE scope_key_hash IN " +
          "(SELECT scope_key_hash FROM idempotencykit_records " +
          "WHERE state='completed' AND expires_at <= " + SQLITE_NOW_MS +
          " ORDER BY expires_at LIMIT ?)",
        ).run(batchSize).changes);
      } finally { db.close(); }
    });
  }
}
