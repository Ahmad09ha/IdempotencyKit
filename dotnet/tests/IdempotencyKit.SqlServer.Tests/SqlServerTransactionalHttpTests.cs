using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.SqlServer;
using IdempotencyKit.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace IdempotencyKit.SqlServer.Tests;

public sealed class SqlServerTestCounters
{
    private int _created, _failed, _slow;
    public int Created => Volatile.Read(ref _created);
    public int Failed => Volatile.Read(ref _failed);
    public int Slow => Volatile.Read(ref _slow);
    public void Create() => Interlocked.Increment(ref _created);
    public void Fail() => Interlocked.Increment(ref _failed);
    public void SlowCreate() => Interlocked.Increment(ref _slow);
}

public sealed class SqlServerTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user),
            new Claim("tenant", Request.Headers["X-Test-Tenant"].ToString())
        }, "Test"));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, "Test")));
    }
}

public sealed record SqlServerHttpOrder(int ProductId, int Quantity);

public sealed class SqlServerTransactionalHttpTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>
{
    private TestServer CreateServer(SqlServerTestCounters counters)
        => new(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddLogging();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, SqlServerTestAuthHandler>(
                        "Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton(counters);
                services.AddSqlServerTransactionalIdempotency(
                    fixture.ConnectionString, TimeSpan.FromMilliseconds(70));
                services.AddIdempotencyKit(options =>
                {
                    options.Namespace = "sqlserver-http-test";
                    options.ResolveCaller = http => new IdempotencyCallerScope(
                        http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
                        http.User.FindFirst("tenant")?.Value);
                    options.Retention = TimeSpan.FromHours(1);
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                // Standard-mode UseIdempotencyKit can be enabled for other
                // routes, but MUST NOT be annotated on these transactional routes.
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapSqlServerTransactionalPost(
                        "/sqlserver/orders", "orders.sqlserver.http.create.v1",
                        async (http, rawJson, connection, transaction, ct) =>
                        {
                            var order = JsonSerializer.Deserialize<SqlServerHttpOrder>(
                                rawJson ?? "{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))
                                ?? throw new InvalidOperationException("Order JSON required.");
                            http.RequestServices.GetRequiredService<SqlServerTestCounters>()
                                .Create();
                            await using var cmd = new SqlCommand("""
                                INSERT INTO dbo.idempotencykit_sqlserver_http_orders_test
                                    (scope_key_hash, quantity)
                                OUTPUT INSERTED.id VALUES (@key, @qty)
                                """, connection, transaction);
                            cmd.Parameters.AddWithValue("@key",
                                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value + ":" +
                                http.Request.Headers["Idempotency-Key"].ToString());
                            cmd.Parameters.AddWithValue("@qty", order.Quantity);
                            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                            return new StoredHttpResponse(201, "application/json",
                                JsonSerializer.SerializeToUtf8Bytes(
                                    new { id, order.ProductId, order.Quantity }),
                                new Dictionary<string, string> {
                                    ["Location"] = "/sqlserver/orders/" + id
                                });
                        },
                        (http, ct) => Task.FromResult(
                            http.User.FindFirst("tenant")?.Value == "acme"));

                    endpoints.MapSqlServerTransactionalPost(
                        "/sqlserver/orders/fail", "orders.sqlserver.http.fail.v1",
                        async (http, rawJson, connection, transaction, ct) =>
                        {
                            http.RequestServices.GetRequiredService<SqlServerTestCounters>()
                                .Fail();
                            await using var insert = new SqlCommand("""
                                INSERT INTO dbo.idempotencykit_sqlserver_http_orders_test
                                    (scope_key_hash, quantity) VALUES (@key, 1)
                                """, connection, transaction);
                            insert.Parameters.AddWithValue("@key",
                                "fail-" + Guid.NewGuid().ToString("N"));
                            await insert.ExecuteNonQueryAsync(ct);
                            throw new InvalidOperationException("Failure after SQL INSERT.");
                        });

                    endpoints.MapSqlServerTransactionalPost(
                        "/sqlserver/orders/slow", "orders.sqlserver.http.slow.v1",
                        async (http, rawJson, connection, transaction, ct) =>
                        {
                            http.RequestServices.GetRequiredService<SqlServerTestCounters>()
                                .SlowCreate();
                            await Task.Delay(300, ct);
                            await using var insert = new SqlCommand("""
                                INSERT INTO dbo.idempotencykit_sqlserver_http_orders_test
                                    (scope_key_hash, quantity)
                                OUTPUT INSERTED.id VALUES (@key, 1)
                                """, connection, transaction);
                            insert.Parameters.AddWithValue("@key",
                                "slow-" + http.Request.Headers["Idempotency-Key"].ToString());
                            var id = (long)(await insert.ExecuteScalarAsync(ct))!;
                            return new StoredHttpResponse(201, "application/json",
                                JsonSerializer.SerializeToUtf8Bytes(new { id }),
                                new Dictionary<string, string>());
                        });
                });
            }));

    private static HttpRequestMessage Post(
        string url, string? key, string body = """{"productId":50,"quantity":2}""",
        string? user = "alice", string tenant = "acme")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        if (user is not null) message.Headers.Add("X-Test-User", user);
        message.Headers.Add("X-Test-Tenant", tenant);
        return message;
    }

    private async Task<long> CountOrdersAsync(string key)
    {
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            SELECT COUNT_BIG(*) FROM dbo.idempotencykit_sqlserver_http_orders_test
            WHERE scope_key_hash = @key
            """, conn);
        cmd.Parameters.AddWithValue("@key", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task CommitsThenReplaysAcrossTwoHostsWithSameJsonDifferentOrder()
    {
        var firstCounter = new SqlServerTestCounters();
        var otherCounter = new SqlServerTestCounters();
        using var hostA = CreateServer(firstCounter);
        using var hostB = CreateServer(otherCounter);
        using var a = hostA.CreateClient();
        using var b = hostB.CreateClient();
        var key = "sql-http-" + Guid.NewGuid().ToString("N");
        using var first = await a.SendAsync(Post("/sqlserver/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var bytes = await first.Content.ReadAsByteArrayAsync();

        using var repeat = await b.SendAsync(Post(
            "/sqlserver/orders", key, """{"quantity":2,"productId":50}"""));
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(bytes, await repeat.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.Location, repeat.Headers.Location);
        Assert.Equal(1, firstCounter.Created);
        Assert.Equal(0, otherCounter.Created);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));

        using var mismatch = await b.SendAsync(Post(
            "/sqlserver/orders", key, """{"productId":50,"quantity":3}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        using var missing = await b.SendAsync(Post("/sqlserver/orders", null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var invalid = await b.SendAsync(Post(
            "/sqlserver/orders", "sql-http-" + Guid.NewGuid().ToString("N"),
            """{"quantity":}"""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task AuthenticationAndFreshAuthorizationRunBeforeReplay()
    {
        var counters = new SqlServerTestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "sql-http-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/sqlserver/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var forbidden = await client.SendAsync(Post(
            "/sqlserver/orders", key, tenant: "revoked"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var unauth = await client.SendAsync(Post(
            "/sqlserver/orders", key, user: null));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Equal(1, counters.Created);
    }

    [Fact]
    public async Task SeparateTrustedUsersCreateSeparateScopedClaims()
    {
        var counters = new SqlServerTestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "sql-http-" + Guid.NewGuid().ToString("N");
        using var alice = await client.SendAsync(Post("/sqlserver/orders", key));
        using var bob = await client.SendAsync(Post(
            "/sqlserver/orders", key, user: "bob"));
        Assert.Equal(HttpStatusCode.Created, alice.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bob.StatusCode);
        Assert.Equal(2, counters.Created);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
        Assert.Equal(1L, await CountOrdersAsync("bob:" + key));
    }

    [Fact]
    public async Task HandlerFailureRollsBackAndSameKeyIsRetryable()
    {
        var counters = new SqlServerTestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "fail-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/sqlserver/orders/fail", key));
        using var second = await client.SendAsync(Post("/sqlserver/orders/fail", key));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(2, counters.Failed);
    }

    [Fact]
    public async Task SameKeyConcurrentHttpRetriesWriteOneDomainOrder()
    {
        var counter = new SqlServerTestCounters();
        using var server = CreateServer(counter);
        using var client = server.CreateClient();
        var key = "sql-http-" + Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(
            _ => client.SendAsync(Post("/sqlserver/orders", key))));
        try
        {
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[]
            {
                HttpStatusCode.Created, HttpStatusCode.Conflict
            }));
            Assert.Equal(1, counter.Created);
            Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task InProgressTransactionReturns409RetryAfterAndThenReplays()
    {
        var counters = new SqlServerTestCounters();
        using var server = CreateServer(counters);
        using var client = server.CreateClient();
        var key = "sql-http-" + Guid.NewGuid().ToString("N");

        var first = client.SendAsync(Post("/sqlserver/orders/slow", key));
        for (var i = 0; i < 100 && counters.Slow == 0; i++)
            await Task.Delay(5);
        Assert.Equal(1, counters.Slow);

        using var during = await client.SendAsync(Post("/sqlserver/orders/slow", key));
        Assert.Equal(HttpStatusCode.Conflict, during.StatusCode);
        Assert.True(during.Headers.RetryAfter is not null);
        using var finished = await first;
        Assert.Equal(HttpStatusCode.Created, finished.StatusCode);
        using var replay = await client.SendAsync(Post("/sqlserver/orders/slow", key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await finished.Content.ReadAsByteArrayAsync(),
            await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, counters.Slow);
    }
}
