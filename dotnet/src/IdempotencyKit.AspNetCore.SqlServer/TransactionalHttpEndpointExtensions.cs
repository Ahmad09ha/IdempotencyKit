using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.Core;
using IdempotencyKit.SqlServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace IdempotencyKit.AspNetCore.SqlServer;

/// <summary>
/// SQL Server-only adapter for explicit Transactional-mode HTTP endpoints.
/// Does NOT use the Standard-mode IdempotentAttribute or endpoint filters.
/// </summary>
public static class TransactionalHttpEndpointExtensions
{
    private static readonly HashSet<string> SafeHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Location", "ETag", "Content-Language" };

    /// <summary>
    /// Register the SQL Server transactional executor with an explicit,
    /// trusted application connection string. Configure AddIdempotencyKit
    /// for namespace and trusted caller scope separately.
    /// </summary>
    public static IServiceCollection AddSqlServerTransactionalIdempotency(
        this IServiceCollection services, string connectionString,
        TimeSpan? claimLockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQL Server connection string is required.",
                nameof(connectionString));
        services.AddSingleton(new SqlServerTransactionalExecutor(
            connectionString, claimLockTimeout));
        return services;
    }

    /// <summary>
    /// Map an authenticated POST JSON operation whose domain SQL uses the
    /// provided connection AND transaction. The callback MUST return valid,
    /// bounded JSON bytes and safe headers before SQL Server commits.
    /// </summary>
    /// <remarks>
    /// The optional authorizeOperation delegate executes before any claim OR
    /// replay; use it for current resource-level authorization if required.
    /// No existing Standard-mode WithIdempotency annotation should be added.
    /// </remarks>
    public static RouteHandlerBuilder MapSqlServerTransactionalPost(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        string operationId,
        Func<HttpContext, string?, SqlConnection, SqlTransaction,
            CancellationToken, Task<StoredHttpResponse>> operation,
        Func<HttpContext, CancellationToken, Task<bool>>? authorizeOperation = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Route pattern is required.", nameof(pattern));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Stable operation ID is required.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(operation);

        return endpoints.MapPost(pattern,
            (HttpContext http, SqlServerTransactionalExecutor executor,
                IdempotencyHttpOptions options, ILoggerFactory loggerFactory) =>
                ExecuteHttpAsync(http, operationId, executor, options, operation,
                    authorizeOperation, loggerFactory.CreateLogger(
                        "IdempotencyKit.TransactionalHttp")))
            .RequireAuthorization();
    }

    private static async Task ExecuteHttpAsync(
        HttpContext http, string operationId,
        SqlServerTransactionalExecutor executor, IdempotencyHttpOptions options,
        Func<HttpContext, string?, SqlConnection, SqlTransaction,
            CancellationToken, Task<StoredHttpResponse>> operation,
        Func<HttpContext, CancellationToken, Task<bool>>? authorize,
        ILogger logger)
    {
        var ct = http.RequestAborted;
        PreparedIdempotencyHttpRequest input;
        try
        {
            input = await IdempotencyHttpRequestPreparation.PrepareAsync(
                http, operationId, options, ct);
        }
        catch (IdempotencyHttpInputException error)
        {
            await WriteProblemAsync(http, error.StatusCode, error.Code, error.Message);
            return;
        }

        // In addition to RequireAuthorization(), check current permissions
        // before retrieving any cached response (same principal can lose access).
        if (authorize is not null)
        {
            bool allowed;
            try { allowed = await authorize(http, ct); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogError("Operation authorization check failed.");
                await WriteProblemAsync(http, 503, "IDEMPOTENCY_AUTHORIZATION_UNAVAILABLE",
                    "Could not check operation permissions.");
                return;
            }
            if (!allowed)
            {
                await WriteProblemAsync(http, 403, "IDEMPOTENCY_OPERATION_FORBIDDEN",
                    "Operation is not authorized.");
                return;
            }
        }

        TransactionalExecutionResult result;
        try
        {
            result = await IdempotencyTelemetry.ObserveTransactionAsync(() =>
                executor.ExecuteAsync(input.ScopeKeyHash,
                    input.RequestFingerprint,
                    (connection, transaction, token) => operation(
                        http, input.BodyJson, connection, transaction, token),
                    options.Retention, ct));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The commit acknowledgement might be ambiguous. Do not run the
            // callback a second time. Client must retry with the SAME key.
            logger.LogError("Transactional execution failed or COMMIT is ambiguous.");
            await WriteProblemAsync(http, 503, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE",
                "Transaction failed or its outcome cannot be confirmed. Retry the same key.");
            return;
        }

        switch (result.Status)
        {
            case TransactionalExecutionStatus.Executed:
            case TransactionalExecutionStatus.Replayed:
                if (result.Response is null)
                {
                    await WriteProblemAsync(http, 503, "IDEMPOTENCY_MISSING_RESULT",
                        "Committed response is not available.");
                    return;
                }
                await WriteResponseAsync(http, result.Response);
                return;
            case TransactionalExecutionStatus.InProgress:
                http.Response.Headers["Retry-After"] = "1";
                await WriteProblemAsync(http, 409, "IDEMPOTENCY_IN_PROGRESS",
                    "A matching operation is currently processing.");
                return;
            case TransactionalExecutionStatus.Uncertain:
                await WriteProblemAsync(http, 409, "IDEMPOTENCY_OUTCOME_UNCERTAIN",
                    "Previous operation outcome requires reconciliation.");
                return;
            case TransactionalExecutionStatus.FingerprintMismatch:
                await WriteProblemAsync(http, 422, "IDEMPOTENCY_FINGERPRINT_MISMATCH",
                    "The key was already used for a different request.");
                return;
            default:
                throw new InvalidOperationException("Unknown transactional outcome.");
        }
    }

    private static async Task WriteResponseAsync(HttpContext http, StoredHttpResponse stored)
    {
        http.Response.StatusCode = stored.StatusCode;
        http.Response.ContentType = stored.ContentType;
        foreach (var (name, value) in stored.Headers)
            if (SafeHeaders.Contains(name)) http.Response.Headers[name] = value;
        http.Response.ContentLength = stored.Body.Length;
        await http.Response.Body.WriteAsync(stored.Body, http.RequestAborted);
    }

    private static async Task WriteProblemAsync(HttpContext http,
        int status, string code, string detail)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank", title = code, status, detail, code
        });
        http.Response.ContentLength = bytes.Length;
        await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
    }
}
