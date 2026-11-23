using System.Text;
using System.Text.Json;
using IdempotencyKit.Core;
using Microsoft.AspNetCore.Http;

namespace IdempotencyKit.AspNetCore;

/// <summary>Pre-handler request identity, fingerprint and original body.</summary>
public sealed record PreparedIdempotencyHttpRequest(
    string ScopeKeyHash,
    string RequestFingerprint,
    string? BodyJson);

/// <summary>Safe, stable client-facing error from request preparation.</summary>
public sealed class IdempotencyHttpInputException(
    int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// Database-independent HTTP input preparation. Database adapters must use
/// the same canonical scoped identity and original JSON bytes in either mode.
/// Invoke only after authentication and authorization, before the handler.
/// </summary>
public static class IdempotencyHttpRequestPreparation
{
    private const int MaxRequestBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<PreparedIdempotencyHttpRequest> PrepareAsync(
        HttpContext http, string operationId,
        IdempotencyHttpOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        if (http.Request.Method is not ("POST" or "PUT" or "PATCH"))
            throw Failure(405, "IDEMPOTENCY_METHOD_UNSUPPORTED",
                "Only POST, PUT and PATCH are supported.");

        if (http.User.Identity?.IsAuthenticated != true)
            throw Failure(401, "IDEMPOTENCY_AUTH_REQUIRED",
                "An authenticated caller is required.");

        if (http.Request.Headers["Idempotency-Key"].Count != 1)
            throw Failure(400, "IDEMPOTENCY_INVALID_REQUEST",
                "Exactly one Idempotency-Key header is required.");

        IdempotencyCallerScope caller;
        try
        {
            caller = options.ResolveCaller?.Invoke(http)
                ?? throw new InvalidOperationException("Missing trusted caller scope.");
            if (string.IsNullOrWhiteSpace(caller.PrincipalId))
                throw new InvalidOperationException("Authenticated caller ID missing.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failure(503, "IDEMPOTENCY_CONFIGURATION_ERROR",
                "Could not resolve an authenticated request scope.");
        }

        string? bodyJson = await ReadBodyAsync(http.Request, cancellationToken);
        try
        {
            var scopeKey = RequestIdentityHasher.ComputeScopeKey(
                new IdempotencyRequestScope(
                    options.Namespace, operationId, caller.PrincipalId,
                    http.Request.Headers["Idempotency-Key"].ToString(), caller.TenantId));
            var fingerprint = RequestFingerprinter.Compute(
                new RequestFingerprintInput(
                    http.Request.Method, operationId, http.Request.Path.Value ?? "/",
                    http.Request.Query.SelectMany(entry =>
                        entry.Value.Select(value =>
                            new QueryParameter(entry.Key, value ?? ""))).ToArray(),
                    bodyJson));

            return new PreparedIdempotencyHttpRequest(scopeKey, fingerprint, bodyJson);
        }
        catch (JsonException)
        {
            throw Failure(400, "IDEMPOTENCY_INVALID_JSON", "Invalid JSON request.");
        }
        catch (ArgumentException)
        {
            throw Failure(400, "IDEMPOTENCY_INVALID_REQUEST", "Invalid key or request.");
        }
        catch (FormatException)
        {
            throw Failure(400, "IDEMPOTENCY_INVALID_JSON", "Invalid JSON request.");
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequestBytes)
            throw Failure(413, "IDEMPOTENCY_BODY_TOO_LARGE",
                "Request body exceeds 64 KiB.");

        if (request.Headers.TryGetValue("Content-Encoding", out var enc) &&
            !string.Equals(enc.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            throw Failure(415, "IDEMPOTENCY_CONTENT_ENCODING_UNSUPPORTED",
                "Content encoding is not supported.");

        if (request.ContentType is not null && !IsJson(request.ContentType))
            throw Failure(415, "IDEMPOTENCY_JSON_REQUIRED",
                "Only application/json request bodies are supported.");

        request.EnableBuffering(bufferThreshold: MaxRequestBytes,
            bufferLimit: MaxRequestBytes + 1L);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + count > MaxRequestBytes)
                    throw Failure(413, "IDEMPOTENCY_BODY_TOO_LARGE",
                        "Request body exceeds 64 KiB.");
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
        }
        catch (IOException)
        {
            throw Failure(413, "IDEMPOTENCY_BODY_TOO_LARGE",
                "Request body exceeds 64 KiB.");
        }
        finally
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
        }

        if (buffer.Length == 0) return null;
        if (request.ContentType is null)
            throw Failure(415, "IDEMPOTENCY_JSON_REQUIRED",
                "JSON content type is required for a body.");

        try { return StrictUtf8.GetString(buffer.ToArray()); }
        catch (DecoderFallbackException)
        {
            throw Failure(400, "IDEMPOTENCY_INVALID_REQUEST",
                "Request body is not valid UTF-8.");
        }
    }

    private static bool IsJson(string type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 128 ||
            type.IndexOfAny(['\r', '\n']) >= 0)
            return false;
        return type.Split(';')[0].Trim()
            .Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static IdempotencyHttpInputException Failure(
        int status, string code, string detail) => new(status, code, detail);
}
