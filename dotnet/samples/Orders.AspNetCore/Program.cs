using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Sqlite;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "5050");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

var file = Environment.GetEnvironmentVariable("ORDERS_DB_PATH") ?? "orders-aspnetcore.db";
await SqliteIdempotencyDatabase.InitializeAsync(file);
var orders = new OrdersDatabase(Path.GetFullPath(file));
await orders.InitializeAsync();

builder.Services.AddAuthentication("OrdersDemo")
    .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>("OrdersDemo", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-aspnetcore";
    options.Retention = TimeSpan.FromHours(24);
    options.ResolveCaller = http => new IdempotencyCallerScope(
        http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
});
builder.Services.AddSqliteTransactionalIdempotency(file);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    service = "orders-aspnetcore",
    status = "ok",
    idempotency = "sqlite-transactional"
}));

app.MapGet("/orders", async (CancellationToken ct) =>
    Results.Ok(new { count = await orders.CountAsync(ct) }))
    .RequireAuthorization();
app.MapGet("/orders/{id:long}", async (long id, CancellationToken ct) =>
    await orders.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound())
    .RequireAuthorization();

app.MapSqliteTransactionalPost("/orders", "orders.create.v1",
    async (http, _, connection, transaction, ct) =>
    {
        var input = (OrderInput)http.Items[typeof(OrderInput)]!;
        await using var insert = connection.CreateCommand();
        // The library owns the transaction. The order and its replay record
        // can commit together only if business SQL uses this exact transaction.
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO orders(product_id, quantity) VALUES ($product, $quantity) RETURNING id";
        insert.Parameters.AddWithValue("$product", input.ProductId);
        insert.Parameters.AddWithValue("$quantity", input.Quantity);
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;
        return new StoredHttpResponse(201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id, productId = input.ProductId, quantity = input.Quantity }),
            new Dictionary<string, string> { ["Location"] = $"/orders/{id}" });
    })
    .AddEndpointFilter(OrderInput.ValidateAsync);

app.Run();

public partial class Program;

internal sealed record OrderInput(int ProductId, int Quantity)
{
    public static async ValueTask<object?> ValidateAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        const int maxBytes = 65_536; // Same limit as the HTTP adapter.
        var request = context.HttpContext.Request;
        if (request.ContentLength > maxBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (request.ContentType?.Split(';')[0].Trim()
                .Equals("application/json", StringComparison.OrdinalIgnoreCase) != true ||
            request.Headers.TryGetValue("Content-Encoding", out var encoding) &&
                !string.Equals(encoding.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        // Validate the domain input before opening a transaction, then rewind
        // so IdempotencyKit fingerprints the original bytes, not a bound DTO.
        request.EnableBuffering(bufferThreshold: maxBytes, bufferLimit: maxBytes + 1L);
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body, cancellationToken: context.HttpContext.RequestAborted);
            if (request.Body.Position > maxBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !PositiveInteger(root, "productId", out var productId) ||
                !PositiveInteger(root, "quantity", out var quantity))
                return Results.BadRequest(new { error = "productId and quantity must be positive integers." });

            context.HttpContext.Items[typeof(OrderInput)] = new OrderInput(productId, quantity);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Expected a JSON order." });
        }
        catch (IOException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        finally
        {
            request.Body.Position = 0;
        }
        return await next(context);
    }

    private static bool PositiveInteger(JsonElement root, string name, out int result)
    {
        result = 0;
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetDecimal(out var number) || number < 1 || number > int.MaxValue ||
            decimal.Truncate(number) != number)
            return false;

        result = (int)number;
        return true;
    }
}

// Intentionally public demo credential for a loopback-only sample. This is not
// an authentication mechanism for a deployed app. Replace it with real auth.
internal sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            !string.Equals(Request.Headers.Authorization.ToString(), "Bearer orders-demo", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "orders-demo")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
