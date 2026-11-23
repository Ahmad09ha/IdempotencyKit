using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.Postgres;
using Npgsql;
using Xunit;

namespace IdempotencyKit.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("IDEMPOTENCYKIT_TEST_POSTGRES")
            ?? throw new InvalidOperationException(
                "Set IDEMPOTENCYKIT_TEST_POSTGRES to an isolated PostgreSQL test database.");
        DataSource = NpgsqlDataSource.Create(connectionString);

        var path = FindMigration();
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var transaction = await conn.BeginTransactionAsync();
        await using (var advisory = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('idempotencykit_schema_001'))",
            conn, transaction))
            await advisory.ExecuteNonQueryAsync();

        await using var command = new NpgsqlCommand(File.ReadAllText(path), conn, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
    }

    private static string FindMigration()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "spec", "sql", "postgres",
                "001_idempotencykit_records.sql");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Missing PostgreSQL migration fixture.");
    }
}

public sealed class PostgresStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static string NewKey() => RequestIdentityHasher.ComputeScopeKey(
        new IdempotencyRequestScope("test", "orders.create.v1",
            "user-" + Guid.NewGuid().ToString("N"),
            "key-" + Guid.NewGuid().ToString("N")));

    private static string Fingerprint(string body) => RequestFingerprinter.Compute(
        new RequestFingerprintInput("POST", "orders.create.v1", "/orders", BodyJson: body));

    private static StoredHttpResponse Response() => new(
        201, "application/json; charset=utf-8", Encoding.UTF8.GetBytes("""{"id":1001}"""),
        new Dictionary<string, string> { ["Location"] = "/orders/1001" });

    [Fact]
    public async Task ClaimConflictMismatchAndResponseReplay()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":2}""");
        var claim = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromSeconds(20));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claim.Status);
        Assert.NotNull(claim.OwnerToken);

        var existing = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromSeconds(20));
        Assert.Equal(IdempotencyClaimStatus.InProgress, existing.Status);
        Assert.Equal(IdempotencyClaimStatus.FingerprintMismatch,
            (await store.TryClaimAsync(key, Fingerprint("""{"quantity":3}"""),
                TimeSpan.FromSeconds(20))).Status);

        Assert.False(await store.TryCompleteAsync(key, fingerprint, Guid.NewGuid(),
            Response(), TimeSpan.FromHours(2)));
        Assert.True(await store.TryCompleteAsync(key, fingerprint, claim.OwnerToken!.Value,
            Response(), TimeSpan.FromHours(2)));
        Assert.False(await store.TryCompleteAsync(key, fingerprint, claim.OwnerToken.Value,
            Response(), TimeSpan.FromHours(2)));

        var replay = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromSeconds(20));
        Assert.Equal(IdempotencyClaimStatus.Completed, replay.Status);
        Assert.Equal(201, replay.Response!.StatusCode);
        Assert.Equal("""{"id":1001}""", Encoding.UTF8.GetString(replay.Response.Body));
        Assert.Equal("/orders/1001", replay.Response.Headers["location"]);
    }

    [Fact]
    public async Task OnlyOneOfThirtyTwoParallelClaimantsCanWin()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":2}""");
        var claims = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ =>
                store.TryClaimAsync(key, fingerprint, TimeSpan.FromMinutes(1))));
        Assert.Single(claims, x => x.Status == IdempotencyClaimStatus.Acquired);
        Assert.Equal(31, claims.Count(x => x.Status == IdempotencyClaimStatus.InProgress));

        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM idempotencykit_records WHERE scope_key_hash = @key", conn);
        cmd.Parameters.AddWithValue("key", key);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ExpiredLeaseBecomesUncertainNotReacquired()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":1}""");
        var claim = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromMilliseconds(50));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claim.Status);

        await Task.Delay(180);
        Assert.Equal(IdempotencyClaimStatus.Uncertain,
            (await store.TryClaimAsync(key, fingerprint, TimeSpan.FromMinutes(1))).Status);
        Assert.Equal(IdempotencyClaimStatus.Uncertain,
            (await store.TryClaimAsync(key, fingerprint, TimeSpan.FromMinutes(1))).Status);
        Assert.False(await store.TryExtendLeaseAsync(key, claim.OwnerToken!.Value,
            TimeSpan.FromMinutes(1)));
        Assert.False(await store.TryCompleteAsync(key, fingerprint, claim.OwnerToken.Value,
            Response(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task ValidOwnerCanExtendUnexpiredLease()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":5}""");
        var claim = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromSeconds(2));
        Assert.False(await store.TryExtendLeaseAsync(key, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryExtendLeaseAsync(key, claim.OwnerToken!.Value,
            TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryCompleteAsync(key, fingerprint, claim.OwnerToken.Value,
            Response(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task RejectsUnsafeHeadersAndOversizedBodies()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":6}""");
        var claim = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromMinutes(1));

        var unsafeResponse = Response() with {
            Headers = new Dictionary<string, string> { ["Set-Cookie"] = "secret=123" }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryCompleteAsync(
            key, fingerprint, claim.OwnerToken!.Value, unsafeResponse, TimeSpan.FromHours(1)));

        var badContentType = Response() with { ContentType = "application/json;\r\nset-cookie: secret=1" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryCompleteAsync(
            key, fingerprint, claim.OwnerToken!.Value, badContentType, TimeSpan.FromHours(1)));

        var giantResponse = Response() with { Body = new byte[262145] };
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryCompleteAsync(
            key, fingerprint, claim.OwnerToken!.Value, giantResponse, TimeSpan.FromHours(1)));

        Assert.True(await store.TryCompleteAsync(
            key, fingerprint, claim.OwnerToken!.Value, Response(), TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task PurgeRemovesOnlyCompletedRowsAfterRetention()
    {
        var store = new PostgresIdempotencyStore(fixture.DataSource);
        string key = NewKey(), fingerprint = Fingerprint("""{"quantity":7}""");
        var claim = await store.TryClaimAsync(key, fingerprint, TimeSpan.FromMinutes(1));
        Assert.True(await store.TryCompleteAsync(key, fingerprint, claim.OwnerToken!.Value,
            Response(), TimeSpan.FromMilliseconds(20)));

        await Task.Delay(120);
        Assert.True(await store.PurgeExpiredCompletedAsync(1000) >= 1);

        await using var conn = await fixture.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM idempotencykit_records WHERE scope_key_hash = @key", conn);
        cmd.Parameters.AddWithValue("key", key);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        Assert.Equal(IdempotencyClaimStatus.Acquired,
            (await store.TryClaimAsync(key, fingerprint, TimeSpan.FromSeconds(30))).Status);
    }
}
