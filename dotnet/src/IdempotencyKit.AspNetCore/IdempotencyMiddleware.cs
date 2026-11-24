using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IdempotencyKit.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace IdempotencyKit.AspNetCore;

/// <summary>
/// Standard-mode middleware. All protected routes must return a bounded JSON
/// 2xx result. No streaming, raw response bypass, or automatic uncertain retry.
/// </summary>
public sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IdempotencyHttpOptions options,
    ILogger<IdempotencyMiddleware> logger)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaxRequestBytes = 65_536;
    private const int MaxResponseBytes = 262_144;
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Location", "ETag", "Content-Language" };

    public async Task InvokeAsync(HttpContext http, IIdempotencyStore store)
    {
        var metadata = http.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>();
        if (metadata is null)
        {
            await next(http);
            return;
        }

        if (http.Request.Method is not ("POST" or "PUT" or "PATCH"))
        {
            await Problem(http, StatusCodes.Status405MethodNotAllowed,
                "IDEMPOTENCY_METHOD_UNSUPPORTED", "Only POST, PUT and PATCH are supported.");
            return;
        }

        // This is defense-in-depth. ASP.NET authorization middleware must run
        // before this middleware; callers must configure RequireAuthorization.
        if (http.User.Identity?.IsAuthenticated != true)
        {
            await Problem(http, StatusCodes.Status401Unauthorized,
                "IDEMPOTENCY_AUTH_REQUIRED", "An authenticated caller is required.");
            return;
        }

        string scopeKey;
        string fingerprint;
        try
        {
            if (http.Request.Headers["Idempotency-Key"].Count != 1)
                throw new ArgumentException("Exactly one Idempotency-Key header is required.");

            var caller = options.ResolveCaller!(http);
            if (caller is null || string.IsNullOrWhiteSpace(caller.PrincipalId))
                throw new InvalidOperationException("Cannot resolve an authenticated principal.");

            var body = await ReadBody(http.Request, http.RequestAborted);
            var request = new RequestFingerprintInput(
                http.Request.Method, metadata.OperationId,
                http.Request.Path.Value ?? "/",
                http.Request.Query.SelectMany(pair =>
                    pair.Value.Select(value => new QueryParameter(pair.Key, value ?? ""))).ToArray(),
                body);
            scopeKey = RequestIdentityHasher.ComputeScopeKey(
                new IdempotencyRequestScope(
                    options.Namespace, metadata.OperationId, caller.PrincipalId,
                    http.Request.Headers["Idempotency-Key"].ToString(), caller.TenantId));
            fingerprint = RequestFingerprinter.Compute(request);
        }
        catch (UnsupportedContentTypeException)
        {
            await Problem(http, StatusCodes.Status415UnsupportedMediaType,
                "IDEMPOTENCY_JSON_REQUIRED", "Only JSON request bodies are supported.");
            return;
        }
        catch (RequestTooLargeException)
        {
            await Problem(http, StatusCodes.Status413PayloadTooLarge,
                "IDEMPOTENCY_BODY_TOO_LARGE", "Request body exceeds 64 KiB.");
            return;
        }
        catch (ArgumentException)
        {
            await Problem(http, StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_INVALID_REQUEST", "Invalid key, scope, or JSON request.");
            return;
        }
        catch (JsonException)
        {
            await Problem(http, StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_INVALID_JSON", "Invalid JSON request.");
            return;
        }
        catch (InvalidOperationException)
        {
            logger.LogError("Idempotency scope configuration failed.");
            await Problem(http, StatusCodes.Status503ServiceUnavailable,
                "IDEMPOTENCY_CONFIGURATION_ERROR", "Cannot resolve request scope.");
            return;
        }

        IdempotencyClaimResult decision;
        try
        {
            decision = await IdempotencyTelemetry.ObserveClaimAsync(() => store.TryClaimAsync(
                scopeKey, fingerprint, options.LeaseDuration, http.RequestAborted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Idempotency store claim failed.");
            await Problem(http, StatusCodes.Status503ServiceUnavailable,
                "IDEMPOTENCY_STORAGE_UNAVAILABLE", "Idempotency storage is unavailable.");
            return;
        }

        switch (decision.Status)
        {
            case IdempotencyClaimStatus.InProgress:
                http.Response.Headers["Retry-After"] = "1";
                await Problem(http, StatusCodes.Status409Conflict,
                    "IDEMPOTENCY_IN_PROGRESS", "A matching operation is still processing.");
                return;
            case IdempotencyClaimStatus.FingerprintMismatch:
                await Problem(http, StatusCodes.Status422UnprocessableEntity,
                    "IDEMPOTENCY_FINGERPRINT_MISMATCH", "Key belongs to a different request.");
                return;
            case IdempotencyClaimStatus.Uncertain:
                await Problem(http, StatusCodes.Status409Conflict,
                    "IDEMPOTENCY_OUTCOME_UNCERTAIN", "Previous outcome requires reconciliation.");
                return;
            case IdempotencyClaimStatus.Completed:
                if (decision.Response is null)
                {
                    await Problem(http, StatusCodes.Status503ServiceUnavailable,
                        "IDEMPOTENCY_MISSING_RESULT", "Completed result is missing.");
                    return;
                }
                await Replay(http, decision.Response);
                return;
            case IdempotencyClaimStatus.Acquired:
                if (decision.OwnerToken is null)
                {
                    await Problem(http, StatusCodes.Status503ServiceUnavailable,
                        "IDEMPOTENCY_MISSING_OWNER", "Idempotency owner token was missing.");
                    return;
                }
                await ExecuteNew(http, store, scopeKey, fingerprint, decision.OwnerToken.Value);
                return;
            default:
                throw new InvalidOperationException("Unknown idempotency claim outcome.");
        }
    }

    private async Task ExecuteNew(
        HttpContext http, IIdempotencyStore store, string scopeKey,
        string fingerprint, Guid ownerToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var response = http.Response;
        var originalBody = response.Body;
        await using var capture = new LimitedResponseBuffer(MaxResponseBytes);
        bool completed = false;
        response.Body = capture;

        try
        {
            await next(http);

            // Unsupported handler responses cannot be safely replayed. The claim
            // is kept as uncertain, not silently released for another execution.
            if (response.HasStarted ||
                response.StatusCode is < 200 or > 299 ||
                !IsJson(response.ContentType))
                throw new InvalidOperationException(
                    "Protected handlers must return a bounded JSON 2xx response.");

            var headers = response.Headers
                .Where(pair => SafeHeaders.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);

            var record = new StoredHttpResponse(
                response.StatusCode, response.ContentType!, capture.ToArray(), headers);
            if (!await store.TryCompleteAsync(
                    scopeKey, fingerprint, ownerToken, record,
                    options.Retention, http.RequestAborted))
                throw new InvalidOperationException("Completion failed or ownership expired.");

            completed = true;
            IdempotencyTelemetry.RecordStandardExecution(startedAt, persisted: true);
            response.Body = originalBody;
            response.ContentLength = capture.Length;
            capture.Position = 0;
            await capture.CopyToAsync(originalBody, http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            if (!completed)
                IdempotencyTelemetry.RecordStandardExecution(startedAt, persisted: false);
            throw;
        }
        catch (Exception)
        {
            response.Body = originalBody;
            if (!completed)
            {
                await MarkUncertainBestEffort(store, scopeKey, fingerprint, ownerToken);
                IdempotencyTelemetry.RecordStandardExecution(startedAt, persisted: false);
            }
            logger.LogError("Idempotency execution or persistence failed.");
            if (!response.HasStarted)
            {
                response.Clear();
                await Problem(http, StatusCodes.Status503ServiceUnavailable,
                    "IDEMPOTENCY_EXECUTION_UNCERTAIN",
                    "Could not safely persist the operation result; reconciliation may be required.");
            }
            else throw;
        }
        finally
        {
            response.Body = originalBody;
        }
    }

    private async Task MarkUncertainBestEffort(
        IIdempotencyStore store, string scopeKey, string fingerprint, Guid ownerToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.TryMarkUncertainAsync(scopeKey, fingerprint, ownerToken, timeout.Token);
        }
        catch (Exception)
        {
            logger.LogError("Unable to mark idempotency outcome uncertain.");
        }
    }

    private static async Task<string?> ReadBody(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequestBytes)
            throw new RequestTooLargeException();

        if (request.Headers.TryGetValue("Content-Encoding", out var encoding) &&
            !string.Equals(encoding.ToString(), "identity",
                StringComparison.OrdinalIgnoreCase))
            throw new UnsupportedContentTypeException();

        if (request.ContentType is not null && !IsJson(request.ContentType))
            throw new UnsupportedContentTypeException();

        request.EnableBuffering(bufferThreshold: MaxRequestBytes, bufferLimit: MaxRequestBytes + 1L);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + count > MaxRequestBytes)
                    throw new RequestTooLargeException();
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
        }
        catch (IOException)
        {
            throw new RequestTooLargeException();
        }
        finally
        {
            request.Body.Position = 0;
        }

        if (buffer.Length == 0) return null;
        if (request.ContentType is null)
            throw new UnsupportedContentTypeException();

        try { return StrictUtf8.GetString(buffer.ToArray()); }
        catch (DecoderFallbackException) { throw new ArgumentException("Body is not valid UTF-8."); }
    }

    private static bool IsJson(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 128 || type.IndexOfAny(['\r', '\n']) >= 0)
            return false;
        var media = type.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Replay(HttpContext http, StoredHttpResponse stored)
    {
        var response = http.Response;
        response.StatusCode = stored.StatusCode;
        response.ContentType = stored.ContentType;
        foreach (var (name, value) in stored.Headers)
            if (SafeHeaders.Contains(name)) response.Headers[name] = value;
        response.ContentLength = stored.Body.Length;
        await response.Body.WriteAsync(stored.Body, http.RequestAborted);
    }

    private static async Task Problem(
        HttpContext http, int status, string code, string detail)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            type = "about:blank", title = code, status, detail, code
        }), http.RequestAborted);
    }

    private sealed class UnsupportedContentTypeException : Exception;
    private sealed class RequestTooLargeException : Exception;

    private sealed class LimitedResponseBuffer(long max) : MemoryStream
    {
        private void EnsureWithinLimit(int bytes)
        {
            if (Position + bytes > max)
                throw new InvalidOperationException("Response exceeds 256 KiB replay limit.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureWithinLimit(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureWithinLimit(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
