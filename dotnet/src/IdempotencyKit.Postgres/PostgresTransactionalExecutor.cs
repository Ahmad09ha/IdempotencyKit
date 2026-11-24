using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdempotencyKit.Core;
using Npgsql;
using NpgsqlTypes;

namespace IdempotencyKit.Postgres;

/// <summary>
/// Observable outcome for an explicit, same-PostgreSQL-transaction operation.
/// Only Executed and Replayed carry an HTTP response.
/// </summary>
public enum TransactionalIdempotencyStatus
{
    Executed,
    Replayed,
    InProgress,
    FingerprintMismatch,
    Uncertain
}

public sealed record TransactionalIdempotencyResult(
    TransactionalIdempotencyStatus Status,
    StoredHttpResponse? Response = null);

/// <summary>
/// Runs a business mutation and its completed replay response in ONE PostgreSQL
/// transaction. The supplied callback MUST use the provided connection AND
/// transaction for every business write. Never run external side effects inside
/// this callback assuming they can be rolled back.
/// </summary>
public sealed class PostgresTransactionalExecutor
    : ITransactionalIdempotencyExecutor<NpgsqlConnection, NpgsqlTransaction>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _claimLockTimeout;

    /// <param name="claimLockTimeout">
    /// Maximum time to wait for a concurrent uncommitted claim. A lock timeout
    /// returns InProgress without invoking the callback. Default 2 seconds.
    /// </param>
    public PostgresTransactionalExecutor(
        NpgsqlDataSource dataSource, TimeSpan? claimLockTimeout = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _claimLockTimeout = claimLockTimeout ?? TimeSpan.FromSeconds(2);
        if (_claimLockTimeout < TimeSpan.FromMilliseconds(1) ||
            _claimLockTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(claimLockTimeout));
    }

    /// <summary>
    /// If a new record is claimed, execute the caller's SQL against the supplied
    /// connection/transaction, validate the response, store it, and commit
    /// atomically. Existing completed records are replayed without callback
    /// execution. Any exception before commit rolls the transaction back.
    ///
    /// IMPORTANT: If PostgreSQL acknowledges the commit but the connection
    /// fails before the acknowledgement reaches this process, the outcome can
    /// be ambiguous. Surface the error; a caller must inspect/retry using
    /// the SAME scoped key against the authoritative database, never use a
    /// new key or blindly replay unrelated external side effects.
    /// </summary>
    public async Task<TransactionalIdempotencyResult> ExecuteAsync(
        string scopeKeyHash,
        string requestFingerprint,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken,
            Task<StoredHttpResponse>> operation,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(scopeKeyHash, nameof(scopeKeyHash));
        ValidateHash(requestFingerprint, nameof(requestFingerprint));
        ArgumentNullException.ThrowIfNull(operation);

        if (retention < TimeSpan.FromMilliseconds(1) ||
            retention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(retention));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        // Enforce bounded unique-index lock waits, not an unbounded queue.
        await using (var setting = new NpgsqlCommand(
            "SELECT set_config('lock_timeout', @duration, true)",
            connection, transaction))
        {
            setting.Parameters.AddWithValue("duration",
                ((long)_claimLockTimeout.TotalMilliseconds).ToString(
                    CultureInfo.InvariantCulture) + "ms");
            await setting.ExecuteScalarAsync(cancellationToken);
        }

        var owner = Guid.NewGuid();
        try
        {
            const string claimSql = """
                INSERT INTO idempotencykit_records (
                    scope_key_hash, request_fingerprint, state,
                    owner_token, lease_expires_at)
                VALUES (@key, @fingerprint, 'processing', @owner,
                        clock_timestamp() + interval '1 day')
                ON CONFLICT (scope_key_hash) DO NOTHING
                RETURNING owner_token
                """;

            await using var claim = new NpgsqlCommand(claimSql, connection, transaction);
            claim.Parameters.AddWithValue("key", scopeKeyHash);
            claim.Parameters.AddWithValue("fingerprint", requestFingerprint);
            claim.Parameters.AddWithValue("owner", owner);

            if (await claim.ExecuteScalarAsync(cancellationToken) is not Guid)
            {
                // A conflicting committed record exists. Read it in the same
                // transaction using a NEW READ COMMITTED statement snapshot.
                // The failed INSERT already waited for competing uncommitted
                // inserts (or timed out) under the PostgreSQL unique index.
                var existing = await InspectExistingAsync(
                    connection, transaction, scopeKeyHash, requestFingerprint,
                    cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                return existing;
            }
        }
        catch (PostgresException error) when (error.SqlState == "55P03")
        {
            // This happens when a different transaction holds the same key.
            // We must not execute the callback while that transaction is open.
            await transaction.RollbackAsync(CancellationToken.None);
            return new TransactionalIdempotencyResult(
                TransactionalIdempotencyStatus.InProgress);
        }

        // The short claim lock timeout should not leak into the caller's
        // business SQL (which may use its own transaction timeout policy).
        await using (var reset = new NpgsqlCommand(
            "SELECT set_config('lock_timeout', '0', true)", connection, transaction))
            await reset.ExecuteScalarAsync(cancellationToken);

        // The processing row is UNCOMMITTED until completion. No other
        // transaction can observe/reclaim it, and no free-floating lease is
        // used to make the decision to run the business callback.
        var response = await operation(connection, transaction, cancellationToken);

        // The same validator checks complete JSON, UTF-8 and HTTP field
        // safety in both modes and on subsequent reads.
        var headersJson = PostgresIdempotencyStore.SerializeSafeHeaders(response);

        const string completeSql = """
            UPDATE idempotencykit_records
               SET state = 'completed', owner_token = NULL,
                   lease_expires_at = NULL,
                   response_status = @status,
                   response_content_type = @content_type,
                   response_body = @body,
                   response_headers = @headers,
                   completed_at = clock_timestamp(),
                   expires_at = clock_timestamp() + @retention
             WHERE scope_key_hash = @key
               AND request_fingerprint = @fingerprint
               AND state = 'processing' AND owner_token = @owner
            """;

        await using (var complete = new NpgsqlCommand(
            completeSql, connection, transaction))
        {
            complete.Parameters.AddWithValue("key", scopeKeyHash);
            complete.Parameters.AddWithValue("fingerprint", requestFingerprint);
            complete.Parameters.AddWithValue("owner", owner);
            complete.Parameters.AddWithValue("status", response.StatusCode);
            complete.Parameters.AddWithValue("content_type", response.ContentType);
            complete.Parameters.AddWithValue("body", response.Body);
            complete.Parameters.Add("headers", NpgsqlDbType.Jsonb).Value = headersJson;
            complete.Parameters.Add("retention", NpgsqlDbType.Interval).Value = retention;
            if (await complete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException(
                    "Transactional idempotency completion lost its claim.");
        }

        var storedHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson)
            ?? throw new InvalidOperationException("Response header serialization failed.");

        // This single COMMIT is the atomic boundary for both the business SQL
        // and the persisted replay result. Never return success before commit.
        await transaction.CommitAsync(cancellationToken);
        return new TransactionalIdempotencyResult(
            TransactionalIdempotencyStatus.Executed,
            response with { Headers = storedHeaders });
    }

    /// <summary>
    /// Provider-neutral view of the already-supported Npgsql transactional API.
    /// The concrete ExecuteAsync remains unchanged for source compatibility.
    /// Future relational providers implement the same typed interface with
    /// their own connection/transaction types and locking algorithm.
    /// </summary>
    async Task<TransactionalExecutionResult>
        ITransactionalIdempotencyExecutor<NpgsqlConnection, NpgsqlTransaction>.ExecuteAsync(
            string scopeKeyHash,
            string requestFingerprint,
            Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken,
                Task<StoredHttpResponse>> operation,
            TimeSpan retention,
            CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            scopeKeyHash, requestFingerprint, operation, retention, cancellationToken);
        var status = result.Status switch
        {
            TransactionalIdempotencyStatus.Executed => TransactionalExecutionStatus.Executed,
            TransactionalIdempotencyStatus.Replayed => TransactionalExecutionStatus.Replayed,
            TransactionalIdempotencyStatus.InProgress => TransactionalExecutionStatus.InProgress,
            TransactionalIdempotencyStatus.FingerprintMismatch =>
                TransactionalExecutionStatus.FingerprintMismatch,
            TransactionalIdempotencyStatus.Uncertain => TransactionalExecutionStatus.Uncertain,
            _ => throw new InvalidOperationException("Unknown PostgreSQL transactional status.")
        };
        return new TransactionalExecutionResult(status, result.Response);
    }

    private static async Task<TransactionalIdempotencyResult> InspectExistingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string fingerprint, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_fingerprint, state,
                   lease_expires_at <= clock_timestamp() AS lease_expired,
                   response_status, response_content_type,
                   response_body, response_headers::text
              FROM idempotencykit_records WHERE scope_key_hash = @key
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "Conflicting idempotency record disappeared during inspection; retry.");

        if (!string.Equals(reader.GetString(0), fingerprint, StringComparison.Ordinal))
            return new TransactionalIdempotencyResult(
                TransactionalIdempotencyStatus.FingerprintMismatch);

        switch (reader.GetString(1))
        {
            case "processing":
                return new TransactionalIdempotencyResult(
                    reader.IsDBNull(2) || reader.GetBoolean(2)
                        ? TransactionalIdempotencyStatus.Uncertain
                        : TransactionalIdempotencyStatus.InProgress);
            case "uncertain":
                return new TransactionalIdempotencyResult(
                    TransactionalIdempotencyStatus.Uncertain);
            case "completed":
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    reader.GetString(6))
                    ?? throw new InvalidOperationException(
                        "Completed response headers are missing.");
                return new TransactionalIdempotencyResult(
                    TransactionalIdempotencyStatus.Replayed,
                    PostgresIdempotencyStore.ValidateStoredResponse(new StoredHttpResponse(
                        reader.GetInt32(3), reader.GetString(4),
                        reader.GetFieldValue<byte[]>(5), headers)));
            default:
                throw new InvalidOperationException("Unknown idempotency record state.");
        }
    }

    private static void ValidateHash(string? value, string argumentName)
    {
        if (value is null ||
            !Regex.IsMatch(value, @"\Av1:[0-9a-f]{64}\z",
                RegexOptions.CultureInvariant))
            throw new ArgumentException(
                "Expected a v1: SHA-256 hash.", argumentName);
    }
}
