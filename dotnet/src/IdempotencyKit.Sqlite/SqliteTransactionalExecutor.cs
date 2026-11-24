using IdempotencyKit.Core;
using Microsoft.Data.Sqlite;

namespace IdempotencyKit.Sqlite;

/// <summary>
/// SQLite file-backed atomic same-database business transaction executor.
/// Only SQL performed on the SUPPLIED SqliteConnection/SqliteTransaction is
/// atomic with the stored replay result. An unrelated SQLite writer may cause
/// SQLITE_BUSY/SQLITE_LOCKED; this is a storage error, NOT same-key InProgress.
/// </summary>
public sealed class SqliteTransactionalExecutor
    : ITransactionalIdempotencyExecutor<SqliteConnection, SqliteTransaction>
{
    private readonly string _file;
    private readonly int _timeoutSeconds;

    public SqliteTransactionalExecutor(string databasePath, int busyTimeoutSeconds = 2)
    {
        _file = SqlitePrimitives.FilePath(databasePath);
        if (busyTimeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutSeconds));
        _timeoutSeconds = busyTimeoutSeconds;
    }

    public async Task<TransactionalExecutionResult> ExecuteAsync(
        string scopeKeyHash, string requestFingerprint,
        Func<SqliteConnection, SqliteTransaction, CancellationToken,
            Task<StoredHttpResponse>> operation,
        TimeSpan retention, CancellationToken cancellationToken = default)
    {
        SqlitePrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlitePrimitives.ValidateHash(requestFingerprint, nameof(requestFingerprint));
        var retentionMs = SqlitePrimitives.DurationMs(
            retention, SqlitePrimitives.MaxRetentionMs, nameof(retention));
        ArgumentNullException.ThrowIfNull(operation);

        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);

        // BEGIN IMMEDIATE reserves the one SQLite file writer BEFORE
        // reading the key. It avoids read-to-write escalation failures.
        // Never retry a busy/locked transaction callback automatically.
        await using var tx = conn.BeginTransaction(deferred: false);
        try
        {
            string? fingerprint = null, state = null;
            bool expired = false;
            StoredHttpResponse? replay = null;
            await using (var read = SqlitePrimitives.Command(conn, tx, $"""
                SELECT request_fingerprint, state,
                       lease_expires_at <= {SqlitePrimitives.Now} AS expired,
                       response_status, response_content_type, response_body,
                       response_headers
                  FROM idempotencykit_records WHERE scope_key_hash = @key
                """))
            {
                SqlitePrimitives.Bind(read, "@key", scopeKeyHash);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    fingerprint = reader.GetString(0);
                    state = reader.GetString(1);
                    expired = !reader.IsDBNull(2) && reader.GetBoolean(2);
                    if (state == "completed" &&
                        string.Equals(fingerprint, requestFingerprint,
                            StringComparison.Ordinal))
                        replay = SqlitePrimitives.ReadResponse(reader, 3, 4, 5, 6);
                }
            }

            if (fingerprint is not null)
            {
                TransactionalExecutionResult existing;
                if (!string.Equals(fingerprint, requestFingerprint,
                    StringComparison.Ordinal))
                    existing = new(TransactionalExecutionStatus.FingerprintMismatch);
                else if (state == "completed")
                    existing = new(TransactionalExecutionStatus.Replayed, replay);
                else if (state == "uncertain")
                    existing = new(TransactionalExecutionStatus.Uncertain);
                else if (state == "processing" && expired)
                {
                    await using var uncertain = SqlitePrimitives.Command(conn, tx, """
                        UPDATE idempotencykit_records
                           SET state='uncertain',owner_token=NULL,lease_expires_at=NULL
                         WHERE scope_key_hash=@key AND state='processing'
                        """);
                    SqlitePrimitives.Bind(uncertain, "@key", scopeKeyHash);
                    await uncertain.ExecuteNonQueryAsync(cancellationToken);
                    existing = new(TransactionalExecutionStatus.Uncertain);
                }
                else if (state == "processing")
                    existing = new(TransactionalExecutionStatus.InProgress);
                else
                    throw new InvalidOperationException("Unknown SQLite record state.");

                await tx.CommitAsync(cancellationToken);
                return existing;
            }

            var owner = Guid.NewGuid().ToString("D");
            await using (var insert = SqlitePrimitives.Command(conn, tx, $"""
                INSERT INTO idempotencykit_records
                    (scope_key_hash, request_fingerprint, state,
                     owner_token, lease_expires_at)
                VALUES (@key,@fingerprint,'processing',@owner,
                        {SqlitePrimitives.Now} + @lease)
                """))
            {
                SqlitePrimitives.Bind(insert, "@key", scopeKeyHash);
                SqlitePrimitives.Bind(insert, "@fingerprint", requestFingerprint);
                SqlitePrimitives.Bind(insert, "@owner", owner);
                SqlitePrimitives.Bind(insert, "@lease", SqlitePrimitives.MaxLeaseMs);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // No SQL command on a second connection and no external effects
            // are part of this atomicity guarantee.
            var response = await operation(conn, tx, cancellationToken);
            var prepared = SqlitePrimitives.PrepareResponse(response);

            await using (var complete = SqlitePrimitives.Command(conn, tx, $"""
                UPDATE idempotencykit_records
                   SET state='completed', owner_token=NULL, lease_expires_at=NULL,
                       response_status=@status, response_content_type=@type,
                       response_body=@body, response_headers=@headers,
                       completed_at={SqlitePrimitives.Now},
                       expires_at={SqlitePrimitives.Now} + @retention
                 WHERE scope_key_hash=@key AND request_fingerprint=@fingerprint
                   AND state='processing' AND owner_token=@owner
                """))
            {
                SqlitePrimitives.Bind(complete, "@key", scopeKeyHash);
                SqlitePrimitives.Bind(complete, "@fingerprint", requestFingerprint);
                SqlitePrimitives.Bind(complete, "@owner", owner);
                SqlitePrimitives.Bind(complete, "@status", prepared.Response.StatusCode);
                SqlitePrimitives.Bind(complete, "@type", prepared.Response.ContentType);
                SqlitePrimitives.Bind(complete, "@body", prepared.Response.Body);
                SqlitePrimitives.Bind(complete, "@headers", prepared.HeadersJson);
                SqlitePrimitives.Bind(complete, "@retention", retentionMs);
                if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException(
                        "SQLite transactional completion lost its owner claim.");
            }

            await tx.CommitAsync(cancellationToken);
            return new(TransactionalExecutionStatus.Executed, prepared.Response);
        }
        catch
        {
            await SqlitePrimitives.RollbackSilently(tx);
            // COMMIT acknowledgement could be ambiguous for some I/O errors.
            // Never rerun the handler automatically; clients reuse same key.
            throw;
        }
    }
}
