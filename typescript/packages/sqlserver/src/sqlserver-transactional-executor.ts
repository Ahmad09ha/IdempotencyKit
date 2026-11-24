import sql from "mssql";
import { randomUUID } from "node:crypto";
import type {
  StoredHttpResponse, TransactionalIdempotencyExecutor,
  TransactionalIdempotencyResult,
} from "@idempotencykit/core";
import {
  acquireKeyLock, durationParams, INSPECT_SQL, MAX_RETENTION_MS,
  prepareResponse, readResponse, validateDuration, validateHash,
} from "./sqlserver-primitives.js";
import type { ExistingRow } from "./sqlserver-primitives.js";

/**
 * SQL Server 2022+/Azure SQL transaction executor. Every business write must
 * use the supplied sql.Transaction via new sql.Request(transaction).
 * NEVER perform an independent pool.request() / pool.query() in the callback.
 */
export class SqlServerTransactionalExecutor
  implements TransactionalIdempotencyExecutor<sql.Transaction> {
  constructor(
    private readonly pool: sql.ConnectionPool,
    private readonly claimLockTimeoutMs = 2_000,
  ) {
    if (!pool) throw new TypeError("Connected SQL Server pool is required.");
    validateDuration(claimLockTimeoutMs, 30_000);
  }

  async execute(
    scopeKeyHash: string,
    requestFingerprint: string,
    operation: (transaction: sql.Transaction) => Promise<StoredHttpResponse>,
    retentionMs: number,
  ): Promise<TransactionalIdempotencyResult> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateDuration(retentionMs, MAX_RETENTION_MS);
    if (typeof operation !== "function")
      throw new TypeError("Business transaction callback is required.");

    const transaction = new sql.Transaction(this.pool);
    await transaction.begin(sql.ISOLATION_LEVEL.READ_COMMITTED);
    let open = true;
    try {
      if (!(await acquireKeyLock(transaction, scopeKeyHash, this.claimLockTimeoutMs))) {
        await transaction.rollback();
        open = false;
        return { status: "in_progress" };
      }

      const read = await new sql.Request(transaction)
        .input("key", sql.NVarChar(67), scopeKeyHash).query(INSPECT_SQL);
      const row = read.recordset?.[0] as ExistingRow | undefined;
      if (row) {
        let existing: TransactionalIdempotencyResult;
        if (row.request_fingerprint !== requestFingerprint)
          existing = { status: "fingerprint_mismatch" };
        else if (row.state === "processing")
          existing = { status: row.lease_expired ? "uncertain" : "in_progress" };
        else if (row.state === "uncertain")
          existing = { status: "uncertain" };
        else if (row.state === "completed")
          existing = { status: "replayed", response: readResponse(row) };
        else
          throw new Error("Unknown SQL Server record state.");

        await transaction.commit();
        open = false;
        return existing;
      }

      const owner = randomUUID();
      await new sql.Request(transaction)
        .input("key", sql.NVarChar(67), scopeKeyHash)
        .input("fingerprint", sql.NVarChar(67), requestFingerprint)
        .input("owner", sql.UniqueIdentifier, owner)
        .query(`
          INSERT INTO dbo.idempotencykit_records
            (scope_key_hash, request_fingerprint, state, owner_token, lease_expires_at)
          VALUES (@key, @fingerprint, 'processing', @owner,
                  DATEADD(day, 1, SYSUTCDATETIME()))`);

      // Keep business SQL and result persistence on the same transaction.
      const response = await operation(transaction);
      const prepared = prepareResponse(response);

      const update = durationParams(
        new sql.Request(transaction)
          .input("key", sql.NVarChar(67), scopeKeyHash)
          .input("fingerprint", sql.NVarChar(67), requestFingerprint)
          .input("owner", sql.UniqueIdentifier, owner)
          .input("status", sql.Int, prepared.response.statusCode)
          .input("content_type", sql.NVarChar(128), prepared.response.contentType)
          .input("body", sql.VarBinary(sql.MAX), Buffer.from(prepared.response.body))
          .input("headers", sql.NVarChar(sql.MAX), prepared.headersJson),
        "retention", retentionMs,
      );
      const completed = await update.query(`
        UPDATE dbo.idempotencykit_records
           SET state = 'completed', owner_token = NULL, lease_expires_at = NULL,
               response_status = @status, response_content_type = @content_type,
               response_body = @body, response_headers = @headers,
               completed_at = SYSUTCDATETIME(),
               expires_at = DATEADD(millisecond, @retention_milliseconds,
                    DATEADD(second, @retention_seconds, SYSUTCDATETIME()))
         WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
           AND state = 'processing' AND owner_token = @owner`);
      if (completed.rowsAffected[0] !== 1)
        throw new Error("SQL Server idempotency completion lost its reservation.");

      await transaction.commit();
      open = false;
      return { status: "executed", response: prepared.response };
    } catch (error) {
      if (open) {
        try { await transaction.rollback(); }
        catch { /* Dead connection or auto-aborted transaction. */ }
      }
      // A COMMIT acknowledgement might be lost. Never automatically repeat
      // a callback; the client must retry with the SAME key on the primary.
      throw error;
    }
  }
}
