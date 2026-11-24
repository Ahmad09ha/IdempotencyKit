using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Sqlite;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace IdempotencyKit.Sqlite.Tests;

public sealed class SqliteTestAuthHandler(
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

public sealed class SqliteHttpCounters
{
    private int _created, _failed;
    public int Created => Volatile.Read(ref _created);
    public int Failed => Volatile.Read(ref _failed);
    public void Create() => Interlocked.Increment(ref _created);
    public void Fail() => Interlocked.Increment(ref _failed);
}

public sealed record SqliteHttpOrder(int ProductId, int Quantity);

public sealed class SqliteTransactionalHttpTests(FileSqliteFixture fixture)
    : IClassFixture<FileSqliteFixture>
{
    private const string Namespace = "sqlite-http-test";
    private const string CreateOperation = "orders.sqlite.http.create.v1";

    private async Task EnsureOrdersAsync()
    {
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS orders_sqlite_http_test (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_scope TEXT NOT NULL,
                quantity INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private TestServer CreateServer(SqliteHttpCounters counters,
        int busyTimeoutSeconds = 1)
        => new(new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddLogging();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, SqliteTestAuthHandler>(
                        "Test", _ => { });
                services.AddAuthorization();
                services.AddSingleton(counters);
                services.AddSqliteTransactionalIdempotency(
                    fixture.DatabasePath, busyTimeoutSeconds);
                services.AddIdempotencyKit(options =>
                {
                    options.Namespace = Namespace;
                    options.Retention = TimeSpan.FromHours(1);
                    options.ResolveCaller = http => new IdempotencyCallerScope(
                        http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "",
                        http.User.FindFirst("tenant")?.Value);
                });
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapSqliteTransactionalPost(
                        "/sqlite/orders", CreateOperation,
                        async (http, bodyJson, connection, transaction, ct) =>
                        {
                            var order = JsonSerializer.Deserialize<SqliteHttpOrder>(
                                bodyJson ?? "{}",
                                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                                ?? throw new InvalidOperationException("Order body required.");
                            http.RequestServices.GetRequiredService<SqliteHttpCounters>()
                                .Create();
                            await using var cmd = connection.CreateCommand();
                            cmd.Transaction = transaction;
                            cmd.CommandText = """
                                INSERT INTO orders_sqlite_http_test
                                    (client_scope, quantity) VALUES (@scope, @quantity)
                                RETURNING id;
                                """;
                            cmd.Parameters.AddWithValue("@scope",
                                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value + ":" +
                                http.Request.Headers["Idempotency-Key"].ToString());
                            cmd.Parameters.AddWithValue("@quantity", order.Quantity);
                            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
                            return new StoredHttpResponse(
                                201, "application/json",
                                JsonSerializer.SerializeToUtf8Bytes(
                                    new { id, order.ProductId, order.Quantity }),
                                new Dictionary<string, string>
                                {
                                    ["Location"] = "/sqlite/orders/" + id
                                });
                        },
                        (http, ct) => Task.FromResult(
                            http.User.FindFirst("tenant")?.Value != "revoked"));

                    endpoints.MapSqliteTransactionalPost(
                        "/sqlite/orders/fail", "orders.sqlite.http.fail.v1",
                        async (http, raw, connection, transaction, ct) =>
                        {
                            http.RequestServices.GetRequiredService<SqliteHttpCounters>()
                                .Fail();
                            await using var insert = connection.CreateCommand();
                            insert.Transaction = transaction;
                            insert.CommandText = """
                                INSERT INTO orders_sqlite_http_test
                                    (client_scope,quantity) VALUES (@scope, 1)
                                """;
                            insert.Parameters.AddWithValue("@scope",
                                "fail-" + Guid.NewGuid().ToString("N"));
                            await insert.ExecuteNonQueryAsync(ct);
                            throw new InvalidOperationException(
                                "Injected failure after SQLite domain insert.");
                        });
                });
            }));

    private static HttpRequestMessage Post(
        string url, string? key,
        string body = """{"productId":50,"quantity":2}""",
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

    private async Task<long> CountOrdersAsync(string scope)
    {
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var query = conn.CreateCommand();
        query.CommandText =
            "SELECT COUNT(*) FROM orders_sqlite_http_test WHERE client_scope=@scope";
        query.Parameters.AddWithValue("@scope", scope);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ReplaysExactBytesAcrossTwoHttpHostsWithoutCallingSecondHandler()
    {
        await EnsureOrdersAsync();
        var firstCounter = new SqliteHttpCounters();
        var secondCounter = new SqliteHttpCounters();
        using var hostA = CreateServer(firstCounter);
        using var hostB = CreateServer(secondCounter);
        using var a = hostA.CreateClient();
        using var b = hostB.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");

        using var first = await a.SendAsync(Post("/sqlite/orders", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsByteArrayAsync();

        using var replay = await b.SendAsync(Post(
            "/sqlite/orders", key, """{"quantity":2,"productId":50}"""));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(1, firstCounter.Created);
        Assert.Equal(0, secondCounter.Created);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));

        using var changed = await b.SendAsync(Post(
            "/sqlite/orders", key, """{"productId":50,"quantity":3}"""));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
    }

    [Fact]
    public async Task AuthenticationAndCurrentPermissionRunBeforeCachedReplay()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");

        using var created = await client.SendAsync(Post("/sqlite/orders", key));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var forbidden = await client.SendAsync(Post(
            "/sqlite/orders", key, tenant: "revoked"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var unauthenticated = await client.SendAsync(Post(
            "/sqlite/orders", key, user: null));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(1, counts.Created);
    }

    [Fact]
    public async Task SameRawKeyIsScopedToTrustedPrincipal()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");
        using var alice = await client.SendAsync(Post("/sqlite/orders", key));
        using var bob = await client.SendAsync(Post("/sqlite/orders", key, user: "bob"));
        Assert.Equal(HttpStatusCode.Created, alice.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bob.StatusCode);
        Assert.Equal(2, counts.Created);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
        Assert.Equal(1L, await CountOrdersAsync("bob:" + key));
    }

    [Fact]
    public async Task InputValidationOccursBeforeBusinessExecution()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        using var missing = await client.SendAsync(Post("/sqlite/orders", null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var malformed = await client.SendAsync(Post(
            "/sqlite/orders", "http-" + Guid.NewGuid().ToString("N"),
            """{"quantity":}"""));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(0, counts.Created);
    }

    [Fact]
    public async Task BusinessSqlExceptionRollsBackAndSameKeyRetryCanRun()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        var key = "http-fail-" + Guid.NewGuid().ToString("N");
        using var first = await client.SendAsync(Post("/sqlite/orders/fail", key));
        using var second = await client.SendAsync(Post("/sqlite/orders/fail", key));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(2, counts.Failed);

        // A failed transaction must not leave its idempotency reservation.
        var scope = RequestIdentityHasher.ComputeScopeKey(new IdempotencyRequestScope(
            Namespace, "orders.sqlite.http.fail.v1", "alice", key, "acme"));
        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT COUNT(*) FROM idempotencykit_records WHERE scope_key_hash=@key";
        query.Parameters.AddWithValue("@key", scope);
        Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ExistingProcessingAndUncertainClaimsReturn409WithoutCallback()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");
        var scope = RequestIdentityHasher.ComputeScopeKey(new IdempotencyRequestScope(
            Namespace, CreateOperation, "alice", key, "acme"));
        var fingerprint = RequestFingerprinter.Compute(new RequestFingerprintInput(
            "POST", CreateOperation, "/sqlite/orders",
            BodyJson: """{"productId":50,"quantity":2}"""));
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var claim = await store.TryClaimAsync(
            scope, fingerprint, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claim.Status);

        using var inProgress = await client.SendAsync(Post("/sqlite/orders", key));
        Assert.Equal(HttpStatusCode.Conflict, inProgress.StatusCode);
        Assert.NotNull(inProgress.Headers.RetryAfter);
        Assert.True(await store.TryMarkUncertainAsync(
            scope, fingerprint, claim.OwnerToken!.Value));
        using var uncertain = await client.SendAsync(Post("/sqlite/orders", key));
        Assert.Equal(HttpStatusCode.Conflict, uncertain.StatusCode);
        Assert.Equal(0, counts.Created);
    }

    [Fact]
    public async Task UnrelatedWriterBusyIs503Not409OrDuplicateCallback()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts, busyTimeoutSeconds: 1);
        using var client = host.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");

        // SQLite's lock is file-wide. This transaction touches only an
        // unrelated domain row, so it is NOT evidence of same-key processing.
        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO orders_sqlite_http_test (client_scope, quantity)
                VALUES ('some-unrelated-writer', 1)
                """;
            await command.ExecuteNonQueryAsync();
            using var busy = await client.SendAsync(Post("/sqlite/orders", key));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.NotEqual(HttpStatusCode.Conflict, busy.StatusCode);
            Assert.Equal(0, counts.Created);
            await transaction.RollbackAsync();
        }

        using var retried = await client.SendAsync(Post("/sqlite/orders", key));
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(1, counts.Created);
        Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
    }

    [Fact]
    public async Task ConcurrentSameKeyHttpRetriesCannotDuplicateSqliteDomainRow()
    {
        await EnsureOrdersAsync();
        var counts = new SqliteHttpCounters();
        using var host = CreateServer(counts);
        using var client = host.CreateClient();
        var key = "http-" + Guid.NewGuid().ToString("N");
        var responses = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => client.SendAsync(Post("/sqlite/orders", key))));
        try
        {
            Assert.All(responses, x => Assert.Contains(x.StatusCode,
                new[] { HttpStatusCode.Created, HttpStatusCode.ServiceUnavailable }));
            Assert.Equal(1, counts.Created);
            Assert.Equal(1L, await CountOrdersAsync("alice:" + key));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }
}
