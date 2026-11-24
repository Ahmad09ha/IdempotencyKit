import { randomUUID } from "node:crypto";
import sql from "mssql";
import type {
  IdempotencyClaimResult, IdempotencyStore, StoredHttpResponse,
} from "@idempotencykit/core";
import {
  acquireKeyLock, durationParams, INSPECT_SQL, MAX_LEASE_MS,
  MAX_RETENTION_MS, prepareResponse, readResponse, validateDuration,
  validateHash,
} from "./sqlserver-primitives.js";
import type { ExistingRow } from "./sqlserver-primitives.js";

/**
 * Durable SQL Server 2022+/Azure SQL Standard-mode store. Must run after
 * spec/sql/sqlserver/001_idempotencykit_records.sql. The app owns the pool.
 * Standard mode does NOT make independently committed domain writes atomic.
 */
export class SqlServerIdempotencyStore implements IdempotencyStore {
  constructor(
    private readonly pool: sql.ConnectionPool,
    private readonly claimLockTimeoutMs = 2_000,
  ) {
    if (!pool) throw new TypeError("Connected SQL Server pool required.");
    validateDuration(claimLockTimeoutMs, 30_000);
  }

  async tryClaim(
    scopeKeyHash: string, requestFingerprint: string, leaseMs: number,
  ): Promise<IdempotencyClaimResult> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateDuration(leaseMs, MAX_LEASE_MS);
    const transaction = new sql.Transaction(this.pool);
    await transaction.begin(sql.ISOLATION_LEVEL.READ_COMMITTED);
    let open = true;

    try {
      // Same transaction-owned key resource as Transactional mode.
      if (!(await acquireKeyLock(transaction, scopeKeyHash, this.claimLockTimeoutMs))) {
        await transaction.rollback();
        open = false;
        return { status: "in_progress" };
      }

      const read = await new sql.Request(transaction)
        .input("key", sql.NVarChar(67), scopeKeyHash).query(INSPECT_SQL);
      const record = read.recordset?.[0] as ExistingRow | undefined;
      let result: IdempotencyClaimResult;

      if (!record) {
        const ownerToken = randomUUID();
        const insert = durationParams(
          new sql.Request(transaction)
            .input("key", sql.NVarChar(67), scopeKeyHash)
            .input("fingerprint", sql.NVarChar(67), requestFingerprint)
            .input("owner", sql.UniqueIdentifier, ownerToken), "lease", leaseMs);
        await insert.query(`
          INSERT INTO dbo.idempotencykit_records
            (scope_key_hash, request_fingerprint, state, owner_token, lease_expires_at)
          VALUES (@key, @fingerprint, 'processing', @owner,
                  DATEADD(millisecond, @lease_milliseconds,
                    DATEADD(second, @lease_seconds, SYSUTCDATETIME())))`);
        result = { status: "acquired", ownerToken };
      } else if (record.request_fingerprint !== requestFingerprint) {
        result = { status: "fingerprint_mismatch" };
      } else if (record.state === "processing" && record.lease_expired) {
        const changed = await new sql.Request(transaction)
          .input("key", sql.NVarChar(67), scopeKeyHash)
          .input("fingerprint", sql.NVarChar(67), requestFingerprint)
          .query(`
            UPDATE dbo.idempotencykit_records
               SET state = 'uncertain', owner_token = NULL,
                   lease_expires_at = NULL
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing'
               AND lease_expires_at <= SYSUTCDATETIME()`);
        if (changed.rowsAffected[0] !== 1)
          throw new Error("Expired SQL Server processing record changed unexpectedly.");
        result = { status: "uncertain" };
      } else if (record.state === "processing") {
        result = { status: "in_progress" };
      } else if (record.state === "uncertain") {
        result = { status: "uncertain" };
      } else if (record.state === "completed") {
        result = { status: "completed", response: readResponse(record) };
      } else {
        throw new Error("Unknown SQL Server record state.");
      }

      await transaction.commit();
      open = false;
      return result;
    } catch (error) {
      if (open) {
        try { await transaction.rollback(); }
        catch { /* Connection may be dead or transaction auto-aborted. */ }
      }
      throw error;
    }
  }

  async tryComplete(
    scopeKeyHash: string, requestFingerprint: string,
    ownerToken: string, response: StoredHttpResponse, retentionMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateOwner(ownerToken);
    validateDuration(retentionMs, MAX_RETENTION_MS);
    const prepared = prepareResponse(response);
    const query = durationParams(
      this.pool.request()
        .input("key", sql.NVarChar(67), scopeKeyHash)
        .input("fingerprint", sql.NVarChar(67), requestFingerprint)
        .input("owner", sql.UniqueIdentifier, ownerToken)
        .input("status", sql.Int, prepared.response.statusCode)
        .input("content_type", sql.NVarChar(128), prepared.response.contentType)
        .input("body", sql.VarBinary(sql.MAX), Buffer.from(prepared.response.body))
        .input("headers", sql.NVarChar(sql.MAX), prepared.headersJson),
      "retention", retentionMs);
    const result = await query.query(`
      UPDATE dbo.idempotencykit_records
         SET state = 'completed', owner_token = NULL, lease_expires_at = NULL,
             response_status = @status, response_content_type = @content_type,
             response_body = @body, response_headers = @headers,
             completed_at = SYSUTCDATETIME(),
             expires_at = DATEADD(millisecond, @retention_milliseconds,
                  DATEADD(second, @retention_seconds, SYSUTCDATETIME()))
       WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
         AND state = 'processing' AND owner_token = @owner
         AND lease_expires_at > SYSUTCDATETIME()`);
    return result.rowsAffected[0] === 1;
  }

  async tryExtendLease(
    scopeKeyHash: string, ownerToken: string, leaseMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateOwner(ownerToken);
    validateDuration(leaseMs, MAX_LEASE_MS);
    const request = durationParams(
      this.pool.request()
        .input("key", sql.NVarChar(67), scopeKeyHash)
        .input("owner", sql.UniqueIdentifier, ownerToken),
      "lease", leaseMs);
    const result = await request.query(`
      UPDATE dbo.idempotencykit_records
         SET lease_expires_at = DATEADD(millisecond, @lease_milliseconds,
                DATEADD(second, @lease_seconds, SYSUTCDATETIME()))
       WHERE scope_key_hash = @key AND state = 'processing'
         AND owner_token = @owner AND lease_expires_at > SYSUTCDATETIME()`);
    return result.rowsAffected[0] === 1;
  }

  async tryMarkUncertain(
    scopeKeyHash: string, requestFingerprint: string, ownerToken: string,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateOwner(ownerToken);
    const result = await this.pool.request()
      .input("key", sql.NVarChar(67), scopeKeyHash)
      .input("fingerprint", sql.NVarChar(67), requestFingerprint)
      .input("owner", sql.UniqueIdentifier, ownerToken)
      .query(`
        UPDATE dbo.idempotencykit_records
           SET state = 'uncertain', owner_token = NULL,
               lease_expires_at = NULL
         WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
           AND state = 'processing' AND owner_token = @owner`);
    return result.rowsAffected[0] === 1;
  }

  async purgeExpiredCompleted(batchSize: number): Promise<number> {
    if (!Number.isSafeInteger(batchSize) || batchSize < 1 || batchSize > 1000)
      throw new RangeError("Batch size must be from 1 to 1000.");
    const result = await this.pool.request()
      .input("batch", sql.Int, batchSize)
      .query(`
        DELETE TOP (@batch) FROM dbo.idempotencykit_records
        WHERE state = 'completed' AND expires_at <= SYSUTCDATETIME()`);
    return result.rowsAffected[0] ?? 0;
  }
}

function validateOwner(value: string): void {
  if (typeof value !== "string" ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value))
    throw new Error("Expected a valid UUID owner token.");
}
