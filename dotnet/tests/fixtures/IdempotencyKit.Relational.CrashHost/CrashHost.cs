using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Postgres;
using IdempotencyKit.AspNetCore.SqlServer;
using IdempotencyKit.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IdempotencyKit.Relational.CrashHost;

// Test-only process. Loopback binding, fixed synthetic identity, disposable databases.
public static class RelationalCrashFixture
{
    public const string Route = "/orders";
    public const string Operation = "create-order";
    public const string Namespace = "relational-http-crash-test";
    public const string Subject = "crash-test-user";
    public const string Tenant = "crash-test-tenant";
    public const string AuthenticationHeader = "X-Test-User";
    public const string ListeningSignal = "IK_CRASH_LISTENING:";
    public const string BlockedSignal = "IK_CRASH_BLOCKED:";
    public const string ConnectionVariable = "IDEMPOTENCYKIT_CRASH_DB";

    public static async Task<int> Main(string[] args)
    {
        if (args is not [("postgres" or "sqlserver"), ("before-commit" or "after-commit" or "retry" or "pool-hold")])
            return 2;
        var provider = args[0];
        var stage = args[1];
        var connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException("Crash fixture requires a disposable test database.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddIdempotencyKit(options =>
        {
            options.Namespace = Namespace;
            options.ResolveCaller = ctx => new IdempotencyCallerScope(
                ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value, Tenant);
        });
        if (provider == "postgres")
        {
            builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
            builder.Services.AddPostgresTransactionalIdempotency();
        }
        else
            builder.Services.AddSqlServerTransactionalIdempotency(connectionString);

        await using var app = builder.Build();
        if (stage == "after-commit")
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == Route)
                    context.Response.OnStarting(() =>
                        context.Response.StatusCode == StatusCodes.Status201Created
                            ? BlockAsync(stage) : Task.CompletedTask);
                await next(context);
            });
        }
        app.UseAuthentication();
        app.UseAuthorization();
        if (provider == "postgres")
            app.MapTransactionalPost(Route, Operation,
                (http, body, db, tx, ct) => InsertOrderAsync(http, body, db, tx, stage, ct));
        else
            app.MapSqlServerTransactionalPost(Route, Operation,
                (http, body, db, tx, ct) => InsertOrderAsync(http, body, db, tx, stage, ct));

        await app.StartAsync();
        Console.WriteLine(ListeningSignal + app.Urls.Single());
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static async Task<StoredHttpResponse> InsertOrderAsync(
        HttpContext http, string? body, DbConnection db, DbTransaction tx,
        string stage, CancellationToken ct)
    {
        // A different generated ID on every callback makes accidental re-execution observable.
        var id = Guid.NewGuid().ToString("N");
        using var json = JsonDocument.Parse(body!);
        var quantity = json.RootElement.GetProperty("quantity").GetInt32();
        await using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO ik_crash_orders (id, request_key, quantity) VALUES (@id, @key, @qty)";
        AddParameter(insert, "@id", id);
        AddParameter(insert, "@key", http.Request.Headers["Idempotency-Key"].ToString());
        AddParameter(insert, "@qty", quantity);
        await insert.ExecuteNonQueryAsync(ct);
        if (stage == "before-commit")
            await BlockAsync(stage);
        if (stage == "pool-hold" && http.Request.Headers["Idempotency-Key"].ToString().StartsWith("pool-held-", StringComparison.Ordinal))
        {
            Console.WriteLine(BlockedSignal + stage);
            await Task.Delay(TimeSpan.FromSeconds(12), ct);
        }

        var status = 201;
        var location = Route + "/" + id;
        // Test-only response injection after the real business INSERT.
        if (json.RootElement.TryGetProperty("fixtureResponse", out var fixture))
        {
            if (fixture.ValueKind == JsonValueKind.Number && fixture.TryGetInt32(out var candidate)
                && candidate is 204 or 205 or 206)
                status = candidate;
            else if (fixture.ValueKind == JsonValueKind.String && fixture.GetString() == "unicode")
                location = Route + "/\u0627";
        }
        return new StoredHttpResponse(status, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id, quantity }),
            new Dictionary<string, string> { ["Location"] = location });
    }

    public static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task BlockAsync(string stage)
    {
        Console.WriteLine(BlockedSignal + stage);
        // Parent must hard-kill this process; a graceful cancellation is not the tested fault.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

internal sealed class FixtureAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!string.Equals(Request.Headers[RelationalCrashFixture.AuthenticationHeader],
            RelationalCrashFixture.Subject, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, RelationalCrashFixture.Subject)], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
