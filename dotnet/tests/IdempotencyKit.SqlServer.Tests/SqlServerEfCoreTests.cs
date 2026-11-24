using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.EntityFrameworkCore.SqlServer;
using IdempotencyKit.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IdempotencyKit.SqlServer.Tests;

public sealed class SqlServerEfOrder
{
    public long Id { get; set; }
    public string ScopeKeyHash { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class SqlServerOrdersDb(DbContextOptions<SqlServerOrdersDb> options)
    : DbContext(options)
{
    public DbSet<SqlServerEfOrder> Orders => Set<SqlServerEfOrder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SqlServerEfOrder>(entity =>
        {
            entity.ToTable("idempotencykit_sqlserver_ef_orders_test", "dbo");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ScopeKeyHash).HasColumnName("scope_key_hash")
                .HasMaxLength(67).IsRequired();
            entity.Property(x => x.Quantity).HasColumnName("quantity");
        });
    }
}

public sealed class SqlServerEfCoreTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>
{
    private static string NewKey() => RequestIdentityHasher.ComputeScopeKey(
        new IdempotencyRequestScope("sqlserver-ef-test", "orders.ef.create.v1",
            "alice", "key-" + Guid.NewGuid().ToString("N"), "tenant"));

    private static string Fingerprint(int qty) => RequestFingerprinter.Compute(
        new RequestFingerprintInput("POST", "orders.ef.create.v1", "/ef/orders",
            BodyJson: $"{{\"quantity\":{qty}}}"));

    private SqlServerEfCoreTransactionalExecutor<SqlServerOrdersDb> CreateExecutor(
        Func<SqlConnection, SqlServerOrdersDb>? factory = null)
    {
        factory ??= connection => new SqlServerOrdersDb(
            new DbContextOptionsBuilder<SqlServerOrdersDb>()
                .UseSqlServer(connection).Options);
        return new SqlServerEfCoreTransactionalExecutor<SqlServerOrdersDb>(
            new SqlServerTransactionalExecutor(fixture.ConnectionString), factory);
    }

    private static StoredHttpResponse Response(long id, int qty) => new(
        201, "application/json",
        Encoding.UTF8.GetBytes($"{{\"id\":{id},\"quantity\":{qty}}}"),
        new Dictionary<string, string> { ["Location"] = "/ef/orders/" + id });

    private async Task<long> CountAsync(string table, string key)
    {
        if (table is not ("idempotencykit_sqlserver_ef_orders_test"
                         or "idempotencykit_records"))
            throw new ArgumentException("Unknown table");
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var query = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo." + table + " WHERE scope_key_hash = @key",
            conn);
        query.Parameters.AddWithValue("@key", key);
        return (long)(await query.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task EfCoreAndReplayRecordCommitTogetherThenReplay()
    {
        var key = NewKey();
        var hash = Fingerprint(2);
        int called = 0;
        var first = await CreateExecutor().ExecuteAsync(key, hash,
            async (db, ct) =>
            {
                Interlocked.Increment(ref called);
                var entity = new SqlServerEfOrder { ScopeKeyHash = key, Quantity = 2 };
                db.Orders.Add(entity);
                await db.SaveChangesAsync(ct);
                return Response(entity.Id, 2);
            }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, first.Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));

        var replay = await CreateExecutor().ExecuteAsync(key, hash,
            (db, ct) => throw new InvalidOperationException(
                "Replayed operation must not invoke EF Core callback."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Response!.Body, replay.Response!.Body);
        Assert.Equal(first.Response.Headers["location"], replay.Response.Headers["location"]);
        Assert.Equal(1, called);

        var mismatch = await CreateExecutor().ExecuteAsync(key, Fingerprint(99),
            (db, ct) => throw new InvalidOperationException("Mismatch must not execute."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.FingerprintMismatch, mismatch.Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
    }

    [Fact]
    public async Task EfCoreSaveChangesThenThrowRollsBackBothTables()
    {
        var key = NewKey();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateExecutor().ExecuteAsync(key, Fingerprint(3),
                async (db, ct) =>
                {
                    db.Orders.Add(new SqlServerEfOrder { ScopeKeyHash = key, Quantity = 3 });
                    await db.SaveChangesAsync(ct);
                    throw new InvalidOperationException("Fail after SaveChanges.");
                }, TimeSpan.FromHours(1)));
        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task PendingTrackedChangesSaveBeforeCommit()
    {
        var key = NewKey();
        var result = await CreateExecutor().ExecuteAsync(key, Fingerprint(4),
            (db, ct) =>
            {
                db.Orders.Add(new SqlServerEfOrder { ScopeKeyHash = key, Quantity = 4 });
                return Task.FromResult(Response(0, 4));
            }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, result.Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task WrongConnectionFailsBeforeAnyDomainWrite()
    {
        var key = NewKey();
        var bad = CreateExecutor(_ => new SqlServerOrdersDb(
            new DbContextOptionsBuilder<SqlServerOrdersDb>()
                .UseSqlServer(fixture.ConnectionString).Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bad.ExecuteAsync(key, Fingerprint(5),
                (db, ct) =>
                {
                    db.Orders.Add(new SqlServerEfOrder { ScopeKeyHash = key, Quantity = 5 });
                    return Task.FromResult(Response(0, 5));
                }, TimeSpan.FromHours(1)));
        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task ConcurrentSameKeyEfRequestsCommitOneRow()
    {
        var key = NewKey();
        int callbacks = 0;
        var executor = CreateExecutor();
        var attempted = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            executor.ExecuteAsync(key, Fingerprint(6),
                async (db, ct) =>
                {
                    Interlocked.Increment(ref callbacks);
                    var order = new SqlServerEfOrder { ScopeKeyHash = key, Quantity = 6 };
                    db.Orders.Add(order);
                    await db.SaveChangesAsync(ct);
                    return Response(order.Id, 6);
                }, TimeSpan.FromHours(1))));
        Assert.Single(attempted, x => x.Status == TransactionalExecutionStatus.Executed);
        Assert.Equal(1, callbacks);
        Assert.All(attempted, x => Assert.Contains(x.Status, new[]
        {
            TransactionalExecutionStatus.Executed,
            TransactionalExecutionStatus.Replayed,
            TransactionalExecutionStatus.InProgress
        }));
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_ef_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
    }
}
