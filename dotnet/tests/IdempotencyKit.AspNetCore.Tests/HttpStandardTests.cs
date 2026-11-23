using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.Core;
using IdempotencyKit.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace IdempotencyKit.AspNetCore.Tests;

public sealed record OrderRequest(int ProductId, int Quantity);

public sealed class TestCounters
{
    private int _created, _slow, _failed, _mvc, _unsupported;
    public int Created => Volatile.Read(ref _created);
    public int Slow => Volatile.Read(ref _slow);
    public int Failed => Volatile.Read(ref _failed);
    public int Mvc => Volatile.Read(ref _mvc);
    public int Unsupported => Volatile.Read(ref _unsupported);
    public int Create() => Interlocked.Increment(ref _created);
    public int CreateSlow() => Interlocked.Increment(ref _slow);
    public int Fail() => Interlocked.Increment(ref _failed);
    public int CreateMvc() => Interlocked.Increment(ref _mvc);
    public int Text() => Interlocked.Increment(ref _unsupported);
}

[ApiController]
[Route("mvc/orders")]
public sealed class OrdersMvcController(TestCounters counters) : ControllerBase
{
    [HttpPost]
    [Authorize]
    [Idempotent("orders.mvc.create.v1")]
    public IActionResult Create([FromBody] OrderRequest order)
    {
        var id = counters.CreateMvc();
        Response.Headers["Location"] = "/mvc/orders/" + id;
        return StatusCode(201, new { id, order.ProductId, order.Quantity });
    }
}

public sealed class TestHeaderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user) ||
            string.IsNullOrWhiteSpace(user.ToString()))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                new Claim("tenant", Request.Headers["X-Test-Tenant"].ToString())
            }, "Test");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class PostgresHttpFixture : IAsyncLifetime
{
    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable("IDEMPOTENCYKIT_TEST_POSTGRES")
            ?? throw new InvalidOperationException("A real PostgreSQL connection is required.");
        DataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await DataSource.OpenConnectionAsync();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "spec", "sql", "postgres",
                "001_idempotencykit_records.sql");
            if (!File.Exists(path)) continue;
            await using var command = new NpgsqlCommand(File.ReadAllText(path), connection);
            await command.ExecuteNonQueryAsync();
            return;
        }
        throw new FileNotFoundException("Migration fixture not found.");
    }

    public async Task DisposeAsync() => await DataSource.DisposeAsync();
}

public sealed class HttpStandardTests(PostgresHttpFixture fixture) : IClassFixture<PostgresHttpFixture>
{
    private TestServer CreateServer(TestCounters counters)
    {
        return new TestServer(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddLogging();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestHeaderAuthHandler>("Test", _ => {});
                services.AddAuthorization();
                services.AddControllers().AddApplicationPart(typeof(OrdersMvcController).Assembly);
                services.AddSingleton(counters);
                services.AddSingleton<IIdempotencyStore>(
                    new PostgresIdempotencyStore(fixture.DataSource));
                services.AddIdempotencyKit(options =>
                {
                    options.Namespace = "idempotencykit-http-test";
                    options.ResolveCaller = context => new IdempotencyCallerScope(
                        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
                        context.User.FindFirst("tenant")?.Value);
                    options.LeaseDuration = TimeSpan.FromSeconds(10);
                    options.Retention = TimeSpan.FromMinutes(2);
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseIdempotencyKit();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));
                    endpoints.MapPost("/orders", (OrderRequest order, TestCounters counter,
                        HttpContext http) =>
                    {
                        var id = counter.Create();
                        http.Response.Headers["Location"] = "/orders/" + id;
                        return Results.Json(new { id, order.ProductId, order.Quantity },
                            statusCode: StatusCodes.Status201Created);
                    }).WithIdempotency("orders.create.v1").RequireAuthorization();

                    endpoints.MapPost("/orders/slow", async (OrderRequest order,
                        TestCounters counter) =>
                    {
                        var id = counter.CreateSlow();
                        await Task.Delay(250);
                        return Results.Json(new { id, order.Quantity },
                            statusCode: StatusCodes.Status201Created);
                    }).WithIdempotency("orders.slow.v1").RequireAuthorization();

                    endpoints.MapPost("/orders/fail", (OrderRequest order,
                        TestCounters counter) =>
                    {
                        counter.Fail();
                        throw new InvalidOperationException("Simulated handler failure.");
                    }).WithIdempotency("orders.fail.v1").RequireAuthorization();

                    endpoints.MapPost("/orders/text", (OrderRequest order,
                        TestCounters counter) =>
                    {
                        counter.Text();
                        return Results.Text("not JSON", "text/plain");
                    }).WithIdempotency("orders.text.v1").RequireAuthorization();

                    endpoints.MapControllers();
                });
            }));
    }

    private static HttpRequestMessage Post(
        string route, string? key, string body = """{"productId":50,"quantity":2}""",
        string? user = "alice", string tenant = "acme")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        request.Headers.Add("X-Test-Tenant", tenant);
        return request;
    }

    [Fact]
    public async Task MinimalApiReplaysExactJsonStatusHeadersAndSameBodyDifferentOrder()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");

        using var first = await client.SendAsync(Post("/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal("/orders/1", first.Headers.Location?.ToString());

        using var repeat = await client.SendAsync(
            Post("/orders", key, """{"quantity":2,"productId":50}"""));
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(firstBody, await repeat.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal(1, counters.Created);
    }

    [Fact]
    public async Task MvcIdempotentAttributeWorksAcrossControllerModelBinding()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/mvc/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        using var replay = await client.SendAsync(Post("/mvc/orders", key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, counters.Mvc);
    }

    [Fact]
    public async Task MismatchMissingKeyAndInvalidJsonFailBeforeHandler()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var mismatch = await client.SendAsync(
            Post("/orders", key, """{"productId":50,"quantity":3}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        using var missing = await client.SendAsync(Post("/orders", null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var invalid = await client.SendAsync(
            Post("/orders", "dotnet-" + Guid.NewGuid().ToString("N"), """{"a":}"""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(1, counters.Created);
    }

    [Fact]
    public async Task DifferentUsersAndTenantsWithSameRawKeyDoNotShareResponse()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var a = await client.SendAsync(Post("/orders", key, user: "alice", tenant: "tenant-a"));
        using var b = await client.SendAsync(Post("/orders", key, user: "bob", tenant: "tenant-a"));
        using var c = await client.SendAsync(Post("/orders", key, user: "alice", tenant: "tenant-b"));
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.Equal(HttpStatusCode.Created, c.StatusCode);
        Assert.Equal(3, counters.Created);
    }

    [Fact]
    public async Task InProgressDuplicateConflictsAndAfterCompletionReplays()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        var started = client.SendAsync(Post("/orders/slow", key));
        for (int i = 0; i < 100 && counters.Slow == 0; i++)
            await Task.Delay(5);
        Assert.Equal(1, counters.Slow);

        using var conflict = await client.SendAsync(Post("/orders/slow", key));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("1", conflict.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        using var original = await started;
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        using var replay = await client.SendAsync(Post("/orders/slow", key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(1, counters.Slow);
    }

    [Fact]
    public async Task HandlerFailureIsUncertainAndNeverAutomaticallyReexecuted()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/orders/fail", key));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        using var repeat = await client.SendAsync(Post("/orders/fail", key));
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
        Assert.Equal(1, counters.Failed);
    }

    [Fact]
    public async Task UnsupportedTextResponseIsNotMarkedCompleted()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/orders/text", key));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        using var again = await client.SendAsync(Post("/orders/text", key));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1, counters.Unsupported);
    }

    [Fact]
    public async Task AuthIsCheckedBeforeReadingIdempotencyResults()
    {
        var counters = new TestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var result = await client.SendAsync(Post("/orders", key, user: null));
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(0, counters.Created);
    }

    [Fact]
    public async Task TwoHttpHostsShareThePostgresClaim()
    {
        var aCounters = new TestCounters();
        var bCounters = new TestCounters();
        using var firstServer = CreateServer(aCounters);
        using var secondServer = CreateServer(bCounters);
        using var firstClient = firstServer.CreateClient();
        using var secondClient = secondServer.CreateClient();
        var key = "dotnet-" + Guid.NewGuid().ToString("N");
        using var first = await firstClient.SendAsync(Post("/orders", key));
        using var replay = await secondClient.SendAsync(Post("/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(),
                     await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, aCounters.Created);
        Assert.Equal(0, bCounters.Created);
    }
}
