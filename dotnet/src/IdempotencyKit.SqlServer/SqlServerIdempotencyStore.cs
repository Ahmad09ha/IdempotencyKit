using System.Data;
using IdempotencyKit.Core;
using Microsoft.Data.SqlClient;

namespace IdempotencyKit.SqlServer;

/// <summary>
/// SQL Server Standard-mode store. Provides atomic claims and safe replay,
/// but does NOT make independently committed business writes transactional.
/// Requires spec/sql/sqlserver/001_idempotencykit_records.sql.
/// </summary>
public sealed class SqlServerIdempotencyStore(
    string connectionString, TimeSpan? claimLockTimeout = null) : IIdempotencyStore
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString : throw new ArgumentException("SQL Server connection string is required.");
    private readonly TimeSpan _claimLockTimeout = ValidateLockTimeout(claimLockTimeout);

    private static TimeSpan ValidateLockTimeout(TimeSpan? timeout)
    {
        var result = timeout ?? TimeSpan.FromSeconds(2);
        if (result < TimeSpan.FromMilliseconds(1) || result > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return result;
    }

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        string scopeKeyHash, string requestFingerprint, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        SqlServerIdempotencyPrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlServerIdempotencyPrimitives.ValidateHash(
            requestFingerprint, nameof(requestFingerprint));
        SqlServerIdempotencyPrimitives.ValidateDuration(
            leaseDuration, TimeSpan.FromDays(1), nameof(leaseDuration));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,
                cancellationToken);

        try
        {
            // Both SQL Server modes acquire the SAME transaction-owned
            // per-key lock. A timeout is evidence of a concurrent same-key
            // owner; unrelated key locks should not block this claim.
            if (!await SqlServerIdempotencyPrimitives.AcquireKeyLockAsync(
                connection, transaction, scopeKeyHash, _claimLockTimeout,
                cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new IdempotencyClaimResult(IdempotencyClaimStatus.InProgress);
            }

            const string readSql = """
                SELECT request_fingerprint, state,
                       CAST(CASE WHEN lease_expires_at <= SYSUTCDATETIME()
                                 THEN 1 ELSE 0 END AS bit),
                       response_status, response_content_type, response_body,
                       response_headers
                  FROM dbo.idempotencykit_records
                 WHERE scope_key_hash = @key
                """;
            string? fingerprint = null, state = null;
            bool leaseExpired = false;
            StoredHttpResponse? response = null;
            await using (var read = new SqlCommand(readSql, connection, transaction))
            {
                read.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    fingerprint = reader.GetString(0);
                    state = reader.GetString(1);
                    leaseExpired = !reader.IsDBNull(2) && reader.GetBoolean(2);
                    if (state == "completed" &&
                        string.Equals(fingerprint, requestFingerprint,
                            StringComparison.Ordinal))
                        response = SqlServerIdempotencyPrimitives.ReadResponse(
                            reader, 3, 4, 5, 6);
                }
            }

            if (fingerprint is null)
            {
                var owner = Guid.NewGuid();
                const string insertSql = """
                    INSERT INTO dbo.idempotencykit_records
                        (scope_key_hash, request_fingerprint, state,
                         owner_token, lease_expires_at)
                    VALUES (@key, @fingerprint, 'processing', @owner,
                            DATEADD(millisecond, @lease_milliseconds,
                                DATEADD(second, @lease_seconds, SYSUTCDATETIME())))
                    """;
                await using var insert = new SqlCommand(insertSql, connection, transaction);
                insert.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
                insert.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
                    .Value = requestFingerprint;
                insert.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = owner;
                SqlServerIdempotencyPrimitives.AddDuration(insert, "@lease", leaseDuration);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new IdempotencyClaimResult(IdempotencyClaimStatus.Acquired, owner);
            }

            if (!string.Equals(fingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return new IdempotencyClaimResult(IdempotencyClaimStatus.FingerprintMismatch);
            }

            if (state == "processing" && leaseExpired)
            {
                const string markSql = """
                    UPDATE dbo.idempotencykit_records
                       SET state = 'uncertain', owner_token = NULL,
                           lease_expires_at = NULL
                     WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
                       AND state = 'processing'
                       AND lease_expires_at <= SYSUTCDATETIME()
                    """;
                await using var mark = new SqlCommand(markSql, connection, transaction);
                mark.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
                mark.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
                    .Value = requestFingerprint;
                if (await mark.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Owned processing record changed unexpectedly.");
                state = "uncertain";
            }

            var result = state switch
            {
                "processing" => new IdempotencyClaimResult(
                    IdempotencyClaimStatus.InProgress),
                "uncertain" => new IdempotencyClaimResult(
                    IdempotencyClaimStatus.Uncertain),
                "completed" => new IdempotencyClaimResult(
                    IdempotencyClaimStatus.Completed, Response: response),
                _ => throw new InvalidOperationException("Unknown SQL Server record state.")
            };
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { /* Connection or transaction may already be unusable. */ }
            throw;
        }
    }

    public async Task<bool> TryCompleteAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        StoredHttpResponse response, TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        SqlServerIdempotencyPrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlServerIdempotencyPrimitives.ValidateHash(
            requestFingerprint, nameof(requestFingerprint));
        ValidateOwner(ownerToken);
        SqlServerIdempotencyPrimitives.ValidateDuration(
            retention, TimeSpan.FromDays(365), nameof(retention));
        var prepared = SqlServerIdempotencyPrimitives.PrepareResponse(response);

        const string sql = """
            UPDATE dbo.idempotencykit_records
               SET state = 'completed', owner_token = NULL,
                   lease_expires_at = NULL,
                   response_status = @status, response_content_type = @content_type,
                   response_body = @body, response_headers = @headers,
                   completed_at = SYSUTCDATETIME(),
                   expires_at = DATEADD(millisecond, @retention_milliseconds,
                        DATEADD(second, @retention_seconds, SYSUTCDATETIME()))
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
               AND lease_expires_at > SYSUTCDATETIME()
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
        command.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
            .Value = requestFingerprint;
        command.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = ownerToken;
        command.Parameters.Add("@status", SqlDbType.Int).Value = response.StatusCode;
        command.Parameters.Add("@content_type", SqlDbType.NVarChar, 128)
            .Value = response.ContentType;
        command.Parameters.Add("@body", SqlDbType.VarBinary, -1).Value = response.Body;
        command.Parameters.Add("@headers", SqlDbType.NVarChar, -1)
            .Value = prepared.HeadersJson;
        SqlServerIdempotencyPrimitives.AddDuration(command, "@retention", retention);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryExtendLeaseAsync(
        string scopeKeyHash, Guid ownerToken, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        SqlServerIdempotencyPrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        ValidateOwner(ownerToken);
        SqlServerIdempotencyPrimitives.ValidateDuration(
            leaseDuration, TimeSpan.FromDays(1), nameof(leaseDuration));
        const string sql = """
            UPDATE dbo.idempotencykit_records
               SET lease_expires_at = DATEADD(millisecond, @lease_milliseconds,
                   DATEADD(second, @lease_seconds, SYSUTCDATETIME()))
             WHERE scope_key_hash = @key AND state = 'processing'
               AND owner_token = @owner
               AND lease_expires_at > SYSUTCDATETIME()
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
        command.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = ownerToken;
        SqlServerIdempotencyPrimitives.AddDuration(command, "@lease", leaseDuration);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryMarkUncertainAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        CancellationToken cancellationToken = default)
    {
        SqlServerIdempotencyPrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlServerIdempotencyPrimitives.ValidateHash(
            requestFingerprint, nameof(requestFingerprint));
        ValidateOwner(ownerToken);
        const string sql = """
            UPDATE dbo.idempotencykit_records
               SET state = 'uncertain', owner_token = NULL,
                   lease_expires_at = NULL
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 67).Value = scopeKeyHash;
        command.Parameters.Add("@fingerprint", SqlDbType.NVarChar, 67)
            .Value = requestFingerprint;
        command.Parameters.Add("@owner", SqlDbType.UniqueIdentifier).Value = ownerToken;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> PurgeExpiredCompletedAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        const string sql = """
            DELETE TOP (@batch) FROM dbo.idempotencykit_records
             WHERE state = 'completed' AND expires_at <= SYSUTCDATETIME()
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@batch", SqlDbType.Int).Value = batchSize;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateOwner(Guid owner)
    {
        if (owner == Guid.Empty) throw new ArgumentException("Owner token is required.");
    }
}
