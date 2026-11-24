using IdempotencyKit.Core;
using Microsoft.Data.Sqlite;

namespace IdempotencyKit.Sqlite;

/// <summary>
/// File-backed SQLite Standard-mode store. Independently committed business
/// effects are NOT atomic with the stored replay response. SQLITE_BUSY,
/// SQLITE_LOCKED and I/O errors propagate: an unrelated writer is NEVER
/// misclassified as InProgress. A genuine committed processing row is.
/// </summary>
public sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    private readonly string _file;
    private readonly int _timeoutSeconds;

    public SqliteIdempotencyStore(string databasePath, int busyTimeoutSeconds = 2)
    {
        _file = SqlitePrimitives.FilePath(databasePath);
        if (busyTimeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutSeconds));
        _timeoutSeconds = busyTimeoutSeconds;
    }

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        string scopeKeyHash, string requestFingerprint, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        SqlitePrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlitePrimitives.ValidateHash(requestFingerprint, nameof(requestFingerprint));
        var ms = SqlitePrimitives.DurationMs(
            leaseDuration, SqlitePrimitives.MaxLeaseMs, nameof(leaseDuration));

        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);
        // BEGIN IMMEDIATE: SQLite has ONE writer per file. An unrelated busy
        // writer may time out. Never infer that busy means the SAME scoped key.
        await using var tx = conn.BeginTransaction(deferred: false);
        try
        {
            string? currentFingerprint = null, state = null;
            long? expires = null;
            StoredHttpResponse? replay = null;
            await using (var read = SqlitePrimitives.Command(conn, tx, """
                SELECT request_fingerprint, state, lease_expires_at,
                       response_status, response_content_type, response_body,
                       response_headers
                  FROM idempotencykit_records WHERE scope_key_hash = @key
                """))
            {
                SqlitePrimitives.Bind(read, "@key", scopeKeyHash);
                await using var rows = await read.ExecuteReaderAsync(cancellationToken);
                if (await rows.ReadAsync(cancellationToken))
                {
                    currentFingerprint = rows.GetString(0);
                    state = rows.GetString(1);
                    expires = rows.IsDBNull(2) ? null : rows.GetInt64(2);
                    if (state == "completed" &&
                        string.Equals(currentFingerprint, requestFingerprint,
                            StringComparison.Ordinal))
                        replay = SqlitePrimitives.ReadResponse(rows, 3, 4, 5, 6);
                }
            }

            IdempotencyClaimResult result;
            if (currentFingerprint is null)
            {
                var owner = Guid.NewGuid();
                await using var insert = SqlitePrimitives.Command(conn, tx, $"""
                    INSERT INTO idempotencykit_records
                        (scope_key_hash,request_fingerprint,state,owner_token,lease_expires_at)
                    VALUES (@key,@fingerprint,'processing',@owner,
                            {SqlitePrimitives.Now} + @lease)
                    """);
                SqlitePrimitives.Bind(insert, "@key", scopeKeyHash);
                SqlitePrimitives.Bind(insert, "@fingerprint", requestFingerprint);
                SqlitePrimitives.Bind(insert, "@owner", owner.ToString("D"));
                SqlitePrimitives.Bind(insert, "@lease", ms);
                await insert.ExecuteNonQueryAsync(cancellationToken);
                result = new(IdempotencyClaimStatus.Acquired, owner);
            }
            else if (!string.Equals(currentFingerprint, requestFingerprint,
                StringComparison.Ordinal))
            {
                result = new(IdempotencyClaimStatus.FingerprintMismatch);
            }
            else if (state == "processing")
            {
                // Check the lease against DB time, not local DateTime.UtcNow,
                // avoiding disagreement between different processes.
                await using var expire = SqlitePrimitives.Command(conn, tx, $"""
                    UPDATE idempotencykit_records
                       SET state = 'uncertain', owner_token = NULL,
                           lease_expires_at = NULL
                     WHERE scope_key_hash = @key
                       AND state = 'processing'
                       AND lease_expires_at <= {SqlitePrimitives.Now}
                    """);
                SqlitePrimitives.Bind(expire, "@key", scopeKeyHash);
                var changed = await expire.ExecuteNonQueryAsync(cancellationToken);
                result = new(changed == 1
                    ? IdempotencyClaimStatus.Uncertain
                    : IdempotencyClaimStatus.InProgress);
            }
            else if (state == "uncertain")
                result = new(IdempotencyClaimStatus.Uncertain);
            else if (state == "completed")
                result = new(IdempotencyClaimStatus.Completed, Response: replay);
            else
                throw new InvalidOperationException("Unknown SQLite idempotency state.");

            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await SqlitePrimitives.RollbackSilently(tx);
            throw;
        }
    }

    public async Task<bool> TryCompleteAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        StoredHttpResponse response, TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        SqlitePrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlitePrimitives.ValidateHash(requestFingerprint, nameof(requestFingerprint));
        SqlitePrimitives.ValidateOwner(ownerToken);
        var ms = SqlitePrimitives.DurationMs(
            retention, SqlitePrimitives.MaxRetentionMs, nameof(retention));
        var prepared = SqlitePrimitives.PrepareResponse(response);
        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);
        await using var command = SqlitePrimitives.Command(conn, null, $"""
            UPDATE idempotencykit_records
               SET state = 'completed', owner_token = NULL, lease_expires_at = NULL,
                   response_status = @status, response_content_type = @type,
                   response_body = @body, response_headers = @headers,
                   completed_at = {SqlitePrimitives.Now},
                   expires_at = {SqlitePrimitives.Now} + @retention
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
               AND lease_expires_at > {SqlitePrimitives.Now}
            """);
        SqlitePrimitives.Bind(command, "@key", scopeKeyHash);
        SqlitePrimitives.Bind(command, "@fingerprint", requestFingerprint);
        SqlitePrimitives.Bind(command, "@owner", ownerToken.ToString("D"));
        SqlitePrimitives.Bind(command, "@status", prepared.Response.StatusCode);
        SqlitePrimitives.Bind(command, "@type", prepared.Response.ContentType);
        SqlitePrimitives.Bind(command, "@body", prepared.Response.Body);
        SqlitePrimitives.Bind(command, "@headers", prepared.HeadersJson);
        SqlitePrimitives.Bind(command, "@retention", ms);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryExtendLeaseAsync(
        string scopeKeyHash, Guid ownerToken, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        SqlitePrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlitePrimitives.ValidateOwner(ownerToken);
        var ms = SqlitePrimitives.DurationMs(
            leaseDuration, SqlitePrimitives.MaxLeaseMs, nameof(leaseDuration));
        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);
        await using var command = SqlitePrimitives.Command(conn, null, $"""
            UPDATE idempotencykit_records
               SET lease_expires_at = {SqlitePrimitives.Now} + @lease
             WHERE scope_key_hash = @key AND state = 'processing'
               AND owner_token = @owner
               AND lease_expires_at > {SqlitePrimitives.Now}
            """);
        SqlitePrimitives.Bind(command, "@key", scopeKeyHash);
        SqlitePrimitives.Bind(command, "@owner", ownerToken.ToString("D"));
        SqlitePrimitives.Bind(command, "@lease", ms);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryMarkUncertainAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        CancellationToken cancellationToken = default)
    {
        SqlitePrimitives.ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        SqlitePrimitives.ValidateHash(requestFingerprint, nameof(requestFingerprint));
        SqlitePrimitives.ValidateOwner(ownerToken);
        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);
        await using var command = SqlitePrimitives.Command(conn, null, """
            UPDATE idempotencykit_records
               SET state = 'uncertain', owner_token = NULL,
                   lease_expires_at = NULL
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
            """);
        SqlitePrimitives.Bind(command, "@key", scopeKeyHash);
        SqlitePrimitives.Bind(command, "@fingerprint", requestFingerprint);
        SqlitePrimitives.Bind(command, "@owner", ownerToken.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> PurgeExpiredCompletedAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var conn = await SqlitePrimitives.OpenAsync(
            _file, _timeoutSeconds, cancellationToken);
        await using var command = SqlitePrimitives.Command(conn, null, $"""
            DELETE FROM idempotencykit_records
             WHERE scope_key_hash IN (
                SELECT scope_key_hash FROM idempotencykit_records
                 WHERE state = 'completed' AND expires_at <= {SqlitePrimitives.Now}
                 ORDER BY expires_at LIMIT @batch
             )
            """);
        SqlitePrimitives.Bind(command, "@batch", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
