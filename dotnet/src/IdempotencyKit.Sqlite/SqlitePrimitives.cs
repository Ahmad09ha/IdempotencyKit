using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdempotencyKit.Core;
using Microsoft.Data.Sqlite;

namespace IdempotencyKit.Sqlite;

internal static class SqlitePrimitives
{
    internal const long MaxLeaseMs = 86_400_000;
    internal const long MaxRetentionMs = 365 * MaxLeaseMs;
    internal const string Now = "CAST(unixepoch('subsec') * 1000 AS INTEGER)";

    // RFC 9110 section 5.5: reject field-value control characters (HTAB
    // is permitted). Do not rely on a web server rejecting data after COMMIT.
    private static bool HasInvalidFieldValue(string value) =>
        value.Any(c => (c < ' ' && c != '\t') || c == '\u007f');

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> SafeHeaders =
        new(StringComparer.Ordinal) { "location", "etag", "content-language" };

    internal static void ValidateHash(string? hash, string name)
    {
        if (hash is null || !Regex.IsMatch(hash, @"\Av1:[0-9a-f]{64}\z"))
            throw new ArgumentException("Expected a lowercase v1: SHA-256 digest.", name);
    }

    internal static long DurationMs(TimeSpan value, long maximumMs, string name)
    {
        if (value.TotalMilliseconds is < 1 or > 31_536_000_000 ||
            value.TotalMilliseconds > maximumMs)
            throw new ArgumentOutOfRangeException(name);
        var millis = checked((long)value.TotalMilliseconds);
        if (millis < 1) throw new ArgumentOutOfRangeException(name);
        return millis;
    }

    internal static void ValidateOwner(Guid owner)
    {
        if (owner == Guid.Empty)
            throw new ArgumentException("Nonempty owner token is required.", nameof(owner));
    }

    internal static string FilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path == ":memory:" || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "SQLite v1 requires an actual local file path, not a URI or :memory:.", nameof(path));
        return Path.GetFullPath(path);
    }

    internal static SqliteConnection Create(string fullPath, int timeoutSeconds) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false,
            DefaultTimeout = timeoutSeconds
        }.ToString());

    internal static async Task<SqliteConnection> OpenAsync(
        string fullPath, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var connection = Create(fullPath, timeoutSeconds);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var pragmas = connection.CreateCommand();
            pragmas.CommandText = "PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;";
            await pragmas.ExecuteNonQueryAsync(cancellationToken);
            await using var verify = connection.CreateCommand();
            verify.CommandText = "PRAGMA journal_mode;";
            var mode = (string?)await verify.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "SQLite idempotency requires WAL mode. Initialize the on-disk database first.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static SqliteCommand Command(SqliteConnection conn,
        SqliteTransaction? transaction, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }

    internal static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    internal static (string HeadersJson, StoredHttpResponse Response)
        PrepareResponse(StoredHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is < 200 or > 299)
            throw new ArgumentException("Only successful 2xx responses are supported.");
        if (response.Body is null || response.Body.Length > 262_144)
            throw new ArgumentException("JSON response must be <= 256 KiB.");
        if (string.IsNullOrWhiteSpace(response.ContentType) ||
            response.ContentType.Length > 128 ||
            HasInvalidFieldValue(response.ContentType))
            throw new ArgumentException("Unsafe response content type.");
        var media = response.ContentType.Split(';')[0].Trim();
        if (!media.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !media.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only JSON response content types are supported.");
        _ = StrictUtf8.GetCharCount(response.Body);
        using (JsonDocument.Parse(response.Body)) { }
        if (response.Headers is null)
            throw new ArgumentException("Response header map must not be null.");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, value) in response.Headers)
        {
            if (original is null) throw new ArgumentException("Header name is null.");
            var name = original.ToLowerInvariant();
            if (!SafeHeaders.Contains(name) || headers.ContainsKey(name))
                throw new ArgumentException("Unsafe or duplicate replay header.");
            if (value is null || value.Length > 4096 ||
                HasInvalidFieldValue(value))
                throw new ArgumentException("Unsafe replay header value.");
            headers.Add(name, value);
        }
        return (JsonSerializer.Serialize(headers), response with { Headers = headers });
    }

    internal static StoredHttpResponse ReadResponse(
        SqliteDataReader reader, int status, int type, int body, int headers)
    {
        if (reader.IsDBNull(status) || reader.IsDBNull(type) ||
            reader.IsDBNull(body) || reader.IsDBNull(headers))
            throw new InvalidOperationException("Incomplete stored SQLite response.");
        var headerMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
            reader.GetString(headers)) ?? throw new InvalidOperationException(
                "Corrupt SQLite replay headers.");
        var result = new StoredHttpResponse(
            reader.GetInt32(status), reader.GetString(type),
            reader.GetFieldValue<byte[]>(body), headerMap);
        // Persisted rows can be modified outside the library. Fail closed if
        // even syntactically valid stored JSON contains unsafe replay fields.
        return PrepareResponse(result).Response;
    }

    internal static async Task RollbackSilently(SqliteTransaction tx)
    {
        try { await tx.RollbackAsync(CancellationToken.None); }
        catch { /* Connection may already have been terminated/rolled back. */ }
    }
}

/// <summary>
/// Apply the SQLite v1 schema and set WAL + FULL synchronous durability.
/// Caller must coordinate first-run migration and provide a directory
/// on a LOCAL disk with SQLite's fsync and POSIX/Windows locks working.
/// Never run this initialization inside a business transaction.
/// </summary>
public static class SqliteIdempotencyDatabase
{
    public static async Task InitializeAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        var file = SqlitePrimitives.FilePath(databasePath);
        await using var conn = SqlitePrimitives.Create(file, 5);
        await conn.OpenAsync(cancellationToken);
        await using (var settings = conn.CreateCommand())
        {
            settings.CommandText = "PRAGMA journal_mode = WAL;";
            var value = (string?)await settings.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(value, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Could not enable SQLite WAL mode.");
            settings.CommandText = "PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;";
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var stream = typeof(SqliteIdempotencyDatabase).Assembly
            .GetManifestResourceStream("IdempotencyKit.Sqlite.Migration001")
            ?? throw new InvalidOperationException("SQLite schema resource is missing.");
        using var reader = new StreamReader(stream);
        var schema = await reader.ReadToEndAsync(cancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
