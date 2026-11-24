using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Sqlite;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace IdempotencyKit.Sqlite.CrashHost;

// Real Kestrel child process, not a deployed app. Never use this test authentication in an application.
public static class SqliteProcessFixture
{
    public const string Route = "/orders";
    public const string Subject = "crash-test-user";
    public const string AuthenticationHeader = "X-Test-User";
    public const string ListeningSignal = "IK_CRASH_LISTENING:";
    public const string BlockedSignal = "IK_CRASH_BLOCKED:";

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var database, ("before-commit" or "after-commit" or "retry")])
            return 2;
        var stage = args[1];

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSqliteTransactionalIdempotency(database);
        builder.Services.AddIdempotencyKit(options =>
        {
            options.Namespace = "sqlite-crash-test";
            options.ResolveCaller = ctx => new IdempotencyCallerScope(
                ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value, "crash-test-tenant");
        });

        await SqliteIdempotencyDatabase.InitializeAsync(database);
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false
        }.ToString()))
        {
            await db.OpenAsync();
            await using var schema = db.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS crash_orders (
                    id TEXT PRIMARY KEY,
                    quantity INTEGER NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        await using var app = builder.Build();
        if (stage == "after-commit")
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == Route)
                {
                    context.Response.OnStarting(() =>
                    {
                        // The real transactional adapter cannot prepare a 201 until the
                        // commit returned. Kestrel has not yet sent response headers.
                        return context.Response.StatusCode == StatusCodes.Status201Created
                            ? BlockAsync(stage)
                            : Task.CompletedTask;
                    });
                }
                await next(context);
            });
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSqliteTransactionalPost(Route, "create-order", async (_, body, db, transaction, ct) =>
        {
            var id = Guid.NewGuid().ToString("N");
            using var request = JsonDocument.Parse(body!);
            var quantity = request.RootElement.GetProperty("quantity").GetInt32();
            await using var insert = db.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO crash_orders (id, quantity) VALUES (@id, @quantity)";
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@quantity", quantity);
            await insert.ExecuteNonQueryAsync(ct);
            if (stage == "before-commit")
                await BlockAsync(stage);

            return new StoredHttpResponse(201, "application/json",
                JsonSerializer.SerializeToUtf8Bytes(new { id, quantity }),
                new Dictionary<string, string> { ["Location"] = $"{Route}/{id}" });
        });

        await app.StartAsync();
        Console.WriteLine(ListeningSignal + app.Urls.Single());
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static async Task BlockAsync(string stage)
    {
        Console.WriteLine(BlockedSignal + stage);
        // Intentionally no request cancellation: only hard termination opens the gate.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

internal sealed class FixtureAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!string.Equals(Request.Headers[SqliteProcessFixture.AuthenticationHeader],
            SqliteProcessFixture.Subject, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, SqliteProcessFixture.Subject)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
