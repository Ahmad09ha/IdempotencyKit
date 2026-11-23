using System.Text.Json;
using System.Text.RegularExpressions;
using IdempotencyKit.Core;
using Npgsql;
using NpgsqlTypes;

namespace IdempotencyKit.Postgres;

/// <summary>
/// Native PostgreSQL implementation of the Standard-mode idempotency store.
/// Requires the explicit migration at spec/sql/postgres/001_idempotencykit_records.sql.
/// This store does NOT make separate business writes transactional.
/// </summary>
public sealed class PostgresIdempotencyStore(NpgsqlDataSource dataSource) : IIdempotencyStore
{
    private const int MaxResponseBytes = 262_144;
    private static readonly HashSet<string> SafeHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "location", "etag", "content-language" };

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        string scopeKeyHash, string requestFingerprint,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        ValidateHash(requestFingerprint, nameof(requestFingerprint));
        ValidateDuration(leaseDuration, nameof(leaseDuration), TimeSpan.FromHours(24));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ownerToken = Guid.NewGuid();
            const string insertSql = """
                INSERT INTO idempotencykit_records
                    (scope_key_hash, request_fingerprint, state, owner_token, lease_expires_at)
                VALUES (@key, @fingerprint, 'processing', @owner,
                        clock_timestamp() + @lease)
                ON CONFLICT (scope_key_hash) DO NOTHING
                RETURNING owner_token
                """;

            await using (var insert = new NpgsqlCommand(insertSql, connection))
            {
                insert.Parameters.AddWithValue("key", scopeKeyHash);
                insert.Parameters.AddWithValue("fingerprint", requestFingerprint);
                insert.Parameters.AddWithValue("owner", ownerToken);
                insert.Parameters.Add("lease", NpgsqlDbType.Interval).Value = leaseDuration;

                if (await insert.ExecuteScalarAsync(cancellationToken) is Guid)
                    return new IdempotencyClaimResult(IdempotencyClaimStatus.Acquired, ownerToken);
            }

            var record = await ReadAsync(connection, scopeKeyHash, cancellationToken);
            if (record is null) continue; // Rare concurrent purging after our failed insert.

            if (!StringComparer.Ordinal.Equals(record.Fingerprint, requestFingerprint))
                return new IdempotencyClaimResult(IdempotencyClaimStatus.FingerprintMismatch);

            if (record.State == "processing" && record.LeaseExpired)
            {
                const string uncertainSql = """
                    UPDATE idempotencykit_records
                       SET state = 'uncertain', owner_token = NULL, lease_expires_at = NULL
                     WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
                       AND state = 'processing' AND lease_expires_at <= clock_timestamp()
                    """;
                await using var uncertain = new NpgsqlCommand(uncertainSql, connection);
                uncertain.Parameters.AddWithValue("key", scopeKeyHash);
                uncertain.Parameters.AddWithValue("fingerprint", requestFingerprint);
                if (await uncertain.ExecuteNonQueryAsync(cancellationToken) == 1)
                    return new IdempotencyClaimResult(IdempotencyClaimStatus.Uncertain);

                // Another operation may have completed or extended the lease.
                record = await ReadAsync(connection, scopeKeyHash, cancellationToken);
                if (record is null) continue;
            }

            return record.State switch
            {
                "processing" => new IdempotencyClaimResult(IdempotencyClaimStatus.InProgress),
                "uncertain" => new IdempotencyClaimResult(IdempotencyClaimStatus.Uncertain),
                "completed" => new IdempotencyClaimResult(
                    IdempotencyClaimStatus.Completed, Response: record.Response),
                _ => throw new InvalidOperationException("Unknown idempotency record state.")
            };
        }

        throw new InvalidOperationException("Idempotency record changed during claim; retry later.");
    }

    public async Task<bool> TryCompleteAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        StoredHttpResponse response, TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        ValidateHash(requestFingerprint, nameof(requestFingerprint));
        if (ownerToken == Guid.Empty) throw new ArgumentException("Owner token is required.", nameof(ownerToken));
        ValidateDuration(retention, nameof(retention), TimeSpan.FromDays(365));
        var headers = SerializeSafeHeaders(response);

        const string sql = """
            UPDATE idempotencykit_records
               SET state = 'completed',
                   owner_token = NULL,
                   lease_expires_at = NULL,
                   response_status = @status,
                   response_content_type = @content_type,
                   response_body = @body,
                   response_headers = @headers,
                   completed_at = clock_timestamp(),
                   expires_at = clock_timestamp() + @retention
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing'
               AND owner_token = @owner
               AND lease_expires_at > clock_timestamp()
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", scopeKeyHash);
        command.Parameters.AddWithValue("fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("owner", ownerToken);
        command.Parameters.AddWithValue("status", response.StatusCode);
        command.Parameters.AddWithValue("content_type", response.ContentType);
        command.Parameters.AddWithValue("body", response.Body);
        command.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = headers;
        command.Parameters.Add("retention", NpgsqlDbType.Interval).Value = retention;

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryExtendLeaseAsync(
        string scopeKeyHash, Guid ownerToken, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        if (ownerToken == Guid.Empty) throw new ArgumentException("Owner token is required.", nameof(ownerToken));
        ValidateDuration(leaseDuration, nameof(leaseDuration), TimeSpan.FromHours(24));

        const string sql = """
            UPDATE idempotencykit_records
               SET lease_expires_at = clock_timestamp() + @lease
             WHERE scope_key_hash = @key AND state = 'processing'
               AND owner_token = @owner AND lease_expires_at > clock_timestamp()
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", scopeKeyHash);
        command.Parameters.AddWithValue("owner", ownerToken);
        command.Parameters.Add("lease", NpgsqlDbType.Interval).Value = leaseDuration;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryMarkUncertainAsync(
        string scopeKeyHash, string requestFingerprint, Guid ownerToken,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        ValidateHash(requestFingerprint, nameof(requestFingerprint));
        if (ownerToken == Guid.Empty)
            throw new ArgumentException("Owner token is required.", nameof(ownerToken));

        const string sql = """
            UPDATE idempotencykit_records
               SET state = 'uncertain', owner_token = NULL, lease_expires_at = NULL
             WHERE scope_key_hash = @key AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", scopeKeyHash);
        command.Parameters.AddWithValue("fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("owner", ownerToken);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> PurgeExpiredCompletedAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be between 1 and 1000.");

        const string sql = """
            WITH expired AS (
                SELECT scope_key_hash FROM idempotencykit_records
                 WHERE state = 'completed' AND expires_at <= clock_timestamp()
                 ORDER BY expires_at LIMIT @batch FOR UPDATE SKIP LOCKED
            )
            DELETE FROM idempotencykit_records r
             USING expired e WHERE r.scope_key_hash = e.scope_key_hash
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("batch", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ExistingRecord?> ReadAsync(
        NpgsqlConnection connection, string key, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_fingerprint, state,
                   lease_expires_at <= clock_timestamp() AS lease_expired,
                   response_status, response_content_type, response_body, response_headers::text
              FROM idempotencykit_records WHERE scope_key_hash = @key
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var state = reader.GetString(1);
        StoredHttpResponse? response = null;
        if (state == "completed")
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6))
                ?? throw new InvalidOperationException("Stored response headers are missing.");
            response = new StoredHttpResponse(
                reader.GetInt32(3), reader.GetString(4),
                reader.GetFieldValue<byte[]>(5), headers);
        }
        return new ExistingRecord(
            reader.GetString(0), state,
            !reader.IsDBNull(2) && reader.GetBoolean(2), response);
    }

    private static string SerializeSafeHeaders(StoredHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is < 200 or > 299)
            throw new ArgumentException("Only successful 2xx responses are supported.", nameof(response));
        if (response.Body is null || response.Body.Length > MaxResponseBytes)
            throw new ArgumentException("Response body exceeds 256 KiB or is missing.", nameof(response));
        if (string.IsNullOrWhiteSpace(response.ContentType) || response.ContentType.Length > 128 ||
            response.ContentType.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Response content type is missing, oversized, or unsafe.", nameof(response));
        var type = response.ContentType.Split(';')[0].Trim();
        if (!type.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only JSON content types are supported.", nameof(response));
        if (response.Headers is null)
            throw new ArgumentException("Response headers must be present.", nameof(response));

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (!SafeHeaders.Contains(name) || normalized.ContainsKey(name))
                throw new ArgumentException("Unsafe or duplicated stored response header: " + header.Key);
            if (header.Value is null || header.Value.Length > 4096 ||
                header.Value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Unsafe stored response header value.");
            normalized.Add(name, header.Value);
        }
        return JsonSerializer.Serialize(normalized);
    }

    private static void ValidateHash(string value, string name)
    {
        if (value is null || !Regex.IsMatch(value, @"\Av1:[a-f0-9]{64}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Expected a v1: SHA-256 hash.", name);
    }

    private static void ValidateDuration(TimeSpan duration, string name, TimeSpan maximum)
    {
        if (duration < TimeSpan.FromMilliseconds(1) || duration > maximum)
            throw new ArgumentOutOfRangeException(name, "Duration is outside the supported range.");
    }

    private sealed record ExistingRecord(
        string Fingerprint, string State, bool LeaseExpired, StoredHttpResponse? Response);
}
