using System.Data;
using IdempotencyKit.Core;
using Microsoft.Data.SqlClient;

namespace IdempotencyKit.SqlServer;

/// <summary>
/// Explicit SQL Server transactional business execution. The domain operation
/// MUST use the provided SqlConnection and SqlTransaction for every write.
/// External side effects and other databases are not covered.
/// </summary>
public sealed class SqlServerTransactionalExecutor(
    string connectionString, TimeSpan? claimLockTimeout = null)
    : ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction>
{
    private readonly string _connectionString =
        !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString : throw new ArgumentException(
                "SQL Server connection string is required.", nameof(connectionString));

    private readonly TimeSpan _claimLockTimeout = ValidateLockTimeout(claimLockTimeout);

    private static TimeSpan ValidateLockTimeout(TimeSpan? timeout)
    {
        var actual = timeout ?? TimeSpan.FromSeconds(2);
        if (actual < TimeSpan.FromMilliseconds(1) ||
            actual > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return actual;
    }

    public async Task<TransactionalExecutionResult> ExecuteAsync(
        string scopeKeyHash,
        string requestFingerprint,
        Func<SqlConnection, SqlTransaction, CancellationToken,
            Task<StoredHttpResponse>> operation,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        SqlServerIdempotencyPrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlServerIdempotencyPrimitives.ValidateHash(requestFingerprint,
            nameof(requestFingerprint));
        SqlServerIdempotencyPrimitives.ValidateDuration(
            retention, TimeSpan.FromDays(365), nameof(retention));
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken);

        try
        {
            // Transaction-owned app lock uses identical resource names in
            // Standard and Transactional mode. Lock timeout here establishes
            // contention for THIS key (not an unrelated table writer).
            var acquired = await SqlServerIdempotencyPrimitives.AcquireKeyLockAsync(
                connection, transaction, scopeKeyHash, _claimLockTimeout,
                cancellationToken);
            if (!acquired)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new TransactionalExecutionResult(
                    TransactionalExecutionStatus.InProgress);
            }

            // The transaction-owned per-key application lock already
            // serializes all IdempotencyKit writers of this key. Do NOT add
            // HOLDLOCK/UPDLOCK for an absent row: SERIALIZABLE key-range
            // locks can block inserts of unrelated scoped keys sharing a
            // neighboring index gap. The unique PK is a second safeguard.
            const string readSql = """
                SELECT request_fingerprint, state,
                       CAST(CASE WHEN lease_expires_at <= SYSUTCDATETIME()
                                 THEN 1 ELSE 0 END AS bit),
                       response_status, response_content_type, response_body,
                       response_headers
                  FROM dbo.idempotencykit_records
                 WHERE scope_key_hash = @key
                """;
            TransactionalExecutionResult? existing = null;
            await using (var read = new SqlCommand(readSql, connection, transaction))
            {
                read.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!string.Equals(reader.GetString(0), requestFingerprint,
                        StringComparison.Ordinal))
                        existing = new TransactionalExecutionResult(
                            TransactionalExecutionStatus.FingerprintMismatch);
                    else
                        existing = reader.GetString(1) switch
                        {
                            "processing" => new TransactionalExecutionResult(
                                reader.IsDBNull(2) || reader.GetBoolean(2)
                                    ? TransactionalExecutionStatus.Uncertain
                                    : TransactionalExecutionStatus.InProgress),
                            "uncertain" => new TransactionalExecutionResult(
                                TransactionalExecutionStatus.Uncertain),
                            "completed" => new TransactionalExecutionResult(
                                TransactionalExecutionStatus.Replayed,
                                SqlServerIdempotencyPrimitives.ReadResponse(
                                    reader, 3, 4, 5, 6)),
                            _ => throw new InvalidOperationException(
                                "Unknown SQL Server idempotency record state.")
                        };
                }
            }

            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            const string insertSql = """
                INSERT INTO dbo.idempotencykit_records
                    (scope_key_hash, request_fingerprint, state, owner_token,
                     lease_expires_at)
                VALUES (@key, @fingerprint, 'processing', @owner,
                        DATEADD(day, 1, SYSUTCDATETIME()))
                """;
            var owner = Guid.NewGuid();
            await using (var insert = new SqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
                insert.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
                    .Value = requestFingerprint;
                insert.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = owner;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // No response can be sent before COMMIT. Do not perform external
            // effects or independent SQL writes from this callback.
            var response = await operation(connection, transaction, cancellationToken);
            var prepared = SqlServerIdempotencyPrimitives.PrepareResponse(response);

            const string completeSql = """
                UPDATE dbo.idempotencykit_records
                   SET state = 'completed', owner_token = NULL,
                       lease_expires_at = NULL,
                       response_status = @status,
                       response_content_type = @content_type,
                       response_body = @body, response_headers = @headers,
                       completed_at = SYSUTCDATETIME(),
                       expires_at = DATEADD(millisecond, @retention_milliseconds,
                             DATEADD(second, @retention_seconds, SYSUTCDATETIME()))
                 WHERE scope_key_hash = @key
                   AND request_fingerprint = @fingerprint
                   AND state = 'processing' AND owner_token = @owner
                """;
            await using (var complete = new SqlCommand(
                completeSql, connection, transaction))
            {
                complete.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value =
                    scopeKeyHash;
                complete.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
                    .Value = requestFingerprint;
                complete.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = owner;
                complete.Parameters.Add("@status", SqlDbType.Int).Value =
                    prepared.Response.StatusCode;
                complete.Parameters.Add("@content_type", SqlDbType.NVarChar, 128)
                    .Value = prepared.Response.ContentType;
                complete.Parameters.Add("@body", SqlDbType.VarBinary, -1).Value =
                    prepared.Response.Body;
                complete.Parameters.Add("@headers", SqlDbType.NVarChar, -1).Value =
                    prepared.HeadersJson;
                SqlServerIdempotencyPrimitives.AddDuration(complete, "@retention",
                    retention);
                if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException(
                        "SQL Server transactional completion lost its claim.");
            }

            // The business operation and persisted replay record share this one
            // commit. COMMIT acknowledgement can still be ambiguous on a
            // broken network: propagate the error; never rerun the callback.
            await transaction.CommitAsync(cancellationToken);
            return new TransactionalExecutionResult(
                TransactionalExecutionStatus.Executed, prepared.Response);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* Connection/transaction may already be dead. */ }
            throw;
        }
    }
}
