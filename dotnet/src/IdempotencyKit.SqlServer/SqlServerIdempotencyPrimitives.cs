using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdempotencyKit.Core;
using Microsoft.Data.SqlClient;

namespace IdempotencyKit.SqlServer;

/// <summary>
/// SQL Server 2022+ provider primitives. Resource locking is scoped to the
/// current database and owned by the transaction, never by a pooled session.
/// </summary>
internal static class SqlServerIdempotencyPrimitives
{

    // RFC 9110 section 5.5: reject field-value control characters (HTAB
    // is permitted). Do not rely on a web server rejecting data after COMMIT.
    private static bool HasInvalidFieldValue(string value) =>
        value.Any(c => (c < ' ' && c != '\t') || c == '\u007f');

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> SafeHeaders =
        new(StringComparer.Ordinal) { "location", "etag", "content-language" };

    internal static void ValidateHash(string? value, string name)
    {
        if (value is null ||
            !Regex.IsMatch(value, @"\Av1:[0-9a-f]{64}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Expected a v1: SHA-256 hash.", name);
    }

    internal static void ValidateDuration(TimeSpan value, TimeSpan max, string name)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > max)
            throw new ArgumentOutOfRangeException(name);
    }

    internal static void AddDuration(
        SqlCommand command, string name, TimeSpan duration)
    {
        // SQL Server 2022 DATEADD requires an int operand. Split year-long
        // retention into seconds + milliseconds to avoid DATEADD bigint overflow.
        var millis = checked((long)duration.TotalMilliseconds);
        command.Parameters.Add(name + "_seconds", SqlDbType.Int)
            .Value = checked((int)(millis / 1000));
        command.Parameters.Add(name + "_milliseconds", SqlDbType.Int)
            .Value = checked((int)(millis % 1000));
    }

    /// <summary>
    /// Zero/positive is an acquired key lock; -1 is a same-resource timeout.
    /// Cancellation, deadlock and parameter/configuration failures throw.
    /// </summary>
    internal static async Task<bool> AcquireKeyLockAsync(
        SqlConnection connection, SqlTransaction transaction, string key,
        TimeSpan timeout, CancellationToken ct)
    {
        const string sql = """
            DECLARE @code int;
            EXEC @code = sys.sp_getapplock
                @Resource = @resource, @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = @wait;
            SELECT @code;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
            "IdempotencyKit:v1:" + key;
        command.Parameters.Add("@wait", SqlDbType.Int).Value =
            checked((int)timeout.TotalMilliseconds);
        var value = (int)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Missing SQL Server application-lock result."));

        if (value >= 0) return true;
        if (value == -1) return false;
        if (value == -2) throw new OperationCanceledException(
            "SQL Server key lock was canceled.", ct);
        throw new InvalidOperationException(
            "SQL Server failed to acquire the idempotency key lock (code " +
            value + "). Business callback was not executed.");
    }

    /// <summary>
    /// Validate JSON, normalize headers and build a response safe to persist.
    /// Must be invoked BEFORE the transactional COMMIT.
    /// </summary>
    internal static (string HeadersJson, StoredHttpResponse Response)
        PrepareResponse(StoredHttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is < 200 or > 299)
            throw new ArgumentException("Only successful 2xx responses are supported.");
        if (response.Body is null || response.Body.Length > 262_144)
            throw new ArgumentException("Response body must be <= 256 KiB.");
        if (string.IsNullOrWhiteSpace(response.ContentType) ||
            response.ContentType.Length > 128 ||
            HasInvalidFieldValue(response.ContentType))
            throw new ArgumentException("Unsafe response content type.");
        var type = response.ContentType.Split(';')[0].Trim();
        if (!type.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only JSON content types are supported.");
        if (response.Headers is null)
            throw new ArgumentException("Response headers cannot be null.");

        _ = StrictUtf8.GetCharCount(response.Body);
        using (JsonDocument.Parse(response.Body))
        {
            // Validates complete JSON before storing or committing.
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, value) in response.Headers)
        {
            var name = original.ToLowerInvariant();
            if (!SafeHeaders.Contains(name) || headers.ContainsKey(name))
                throw new ArgumentException("Unsafe or duplicated response header.");
            if (value is null || value.Length > 4096 ||
                HasInvalidFieldValue(value))
                throw new ArgumentException("Unsafe response header value.");
            headers.Add(name, value);
        }
        var json = JsonSerializer.Serialize(headers);
        return (json, response with { Headers = headers });
    }

    internal static StoredHttpResponse ReadResponse(
        SqlDataReader reader, int statusOrdinal, int contentOrdinal,
        int bodyOrdinal, int headersOrdinal)
    {
        if (reader.IsDBNull(statusOrdinal) || reader.IsDBNull(contentOrdinal) ||
            reader.IsDBNull(bodyOrdinal) || reader.IsDBNull(headersOrdinal))
            throw new InvalidOperationException("Stored SQL Server replay data is incomplete.");
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(
            reader.GetString(headersOrdinal))
            ?? throw new InvalidOperationException("Invalid stored response headers.");
        var stored = new StoredHttpResponse(reader.GetInt32(statusOrdinal),
            reader.GetString(contentOrdinal),
            reader.GetFieldValue<byte[]>(bodyOrdinal), headers);
        // Even a corrupted/tampered row must not replay unsafe headers,
        // invalid JSON or a non-2xx response to the HTTP layer.
        return PrepareResponse(stored).Response;
    }
}
