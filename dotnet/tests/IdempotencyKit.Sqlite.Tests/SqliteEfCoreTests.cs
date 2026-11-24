using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.EntityFrameworkCore.Sqlite;
using IdempotencyKit.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IdempotencyKit.Sqlite.Tests;

public sealed class SqliteEfOrder
{
    public long Id { get; set; }
    public string ScopeKeyHash { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class SqliteOrdersDb(DbContextOptions<SqliteOrdersDb> options) : DbContext(options)
{
    public DbSet<SqliteEfOrder> Orders => Set<SqliteEfOrder>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SqliteEfOrder>(entity =>
        {
            entity.ToTable("orders_sqlite_ef_test");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.ScopeKeyHash).HasColumnName("scope_key_hash")
                .IsRequired();
            entity.Property(x => x.Quantity).HasColumnName("quantity");
        });
    }
}

public sealed class SqliteEfCoreTests(FileSqliteFixture fixture) : IClassFixture<FileSqliteFixture>
{
    private static string Key() => RequestIdentityHasher.ComputeScopeKey(
        new IdempotencyRequestScope("sqlite-ef-test", "orders.sqlite.ef.create.v1",
            "alice", "ef-" + Guid.NewGuid().ToString("N"), "acme"));

    private static string Fingerprint(int qty) => RequestFingerprinter.Compute(
        new RequestFingerprintInput("POST", "orders.sqlite.ef.create.v1",
            "/sqlite/ef/orders", BodyJson: $"{{\"quantity\":{qty}}}"));

    private static StoredHttpResponse Response(long id, int qty) => new(
        201, "application/json",
        Encoding.UTF8.GetBytes($"{{\"id\":{id},\"quantity\":{qty}}}"),
        new Dictionary<string, string> { ["Location"] = "/sqlite/ef/orders/" + id });

    private SqliteEfCoreTransactionalExecutor<SqliteOrdersDb> CreateExecutor(
        Func<SqliteConnection, SqliteOrdersDb>? factory = null,
        int busyTimeoutSeconds = 2)
    {
        factory ??= conn => new SqliteOrdersDb(
            new DbContextOptionsBuilder<SqliteOrdersDb>().UseSqlite(conn).Options);
        return new SqliteEfCoreTransactionalExecutor<SqliteOrdersDb>(
            new SqliteTransactionalExecutor(fixture.DatabasePath, busyTimeoutSeconds), factory);
    }

    private async Task EnsureOrdersAsync()
    {
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS orders_sqlite_ef_test (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                scope_key_hash TEXT NOT NULL,
                quantity INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, string key)
    {
        if (table is not ("orders_sqlite_ef_test" or "idempotencykit_records"))
            throw new ArgumentOutOfRangeException(nameof(table));
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table + " WHERE scope_key_hash=@key";
        cmd.Parameters.AddWithValue("@key", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task EfCoreWritesAndReplayResultCommitTogetherAndReplayAfterNewContext()
    {
        await EnsureOrdersAsync();
        var key = Key();
        var hash = Fingerprint(2);
        var invoked = 0;
        var first = await CreateExecutor().ExecuteAsync(key, hash, async (db, ct) =>
        {
            Interlocked.Increment(ref invoked);
            var order = new SqliteEfOrder { ScopeKeyHash = key, Quantity = 2 };
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            return Response(order.Id, 2);
        }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, first.Status);
        Assert.Equal(1L, await CountAsync("orders_sqlite_ef_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));

        // Reopen via a newly constructed EF bridge on the same persistent file.
        var replay = await CreateExecutor().ExecuteAsync(key, hash,
            (db, ct) => throw new InvalidOperationException("Replay must skip EF callback."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Response!.Body, replay.Response!.Body);
        Assert.Equal(first.Response.Headers["location"], replay.Response.Headers["location"]);
        Assert.Equal(1, invoked);

        var mismatch = await CreateExecutor().ExecuteAsync(key, Fingerprint(3),
            (db, ct) => throw new InvalidOperationException("Mismatched key cannot write."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.FingerprintMismatch, mismatch.Status);
        Assert.Equal(1L, await CountAsync("orders_sqlite_ef_test", key));
    }

    [Fact]
    public async Task EfCoreSaveChangesThenCallbackThrowsRollsBackDomainAndClaim()
    {
        await EnsureOrdersAsync();
        var key = Key();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateExecutor().ExecuteAsync(key, Fingerprint(4), async (db, ct) =>
            {
                db.Orders.Add(new SqliteEfOrder { ScopeKeyHash = key, Quantity = 4 });
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("Failure after EF Core SaveChanges.");
            }, TimeSpan.FromHours(1)));

        Assert.Equal(0L, await CountAsync("orders_sqlite_ef_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));
        var retry = await CreateExecutor().ExecuteAsync(key, Fingerprint(4),
            async (db, ct) =>
            {
                var order = new SqliteEfOrder { ScopeKeyHash = key, Quantity = 4 };
                db.Orders.Add(order);
                await db.SaveChangesAsync(ct);
                return Response(order.Id, 4);
            }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, retry.Status);
        Assert.Equal(1L, await CountAsync("orders_sqlite_ef_test", key));
    }

    [Fact]
    public async Task PendingTrackedEntitiesAreSavedBeforeTheSingleCommit()
    {
        await EnsureOrdersAsync();
        var key = Key();
        var result = await CreateExecutor().ExecuteAsync(key, Fingerprint(5),
            (db, ct) =>
            {
                db.Orders.Add(new SqliteEfOrder { ScopeKeyHash = key, Quantity = 5 });
                return Task.FromResult(Response(0, 5));
            }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, result.Status);
        Assert.Equal(1L, await CountAsync("orders_sqlite_ef_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task DifferentEfConnectionFailsClosedBeforeBusinessCallback()
    {
        await EnsureOrdersAsync();
        var key = Key();
        var callbacks = 0;
        var invalid = CreateExecutor(_ => new SqliteOrdersDb(
            new DbContextOptionsBuilder<SqliteOrdersDb>()
                .UseSqlite(fixture.DatabasePath).Options));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            invalid.ExecuteAsync(key, Fingerprint(6), (db, ct) =>
            {
                Interlocked.Increment(ref callbacks);
                db.Orders.Add(new SqliteEfOrder { ScopeKeyHash = key, Quantity = 6 });
                return Task.FromResult(Response(0, 6));
            }, TimeSpan.FromHours(1)));
        Assert.Equal(0, callbacks);
        Assert.Equal(0L, await CountAsync("orders_sqlite_ef_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task ManyConcurrentEfCoreRequestsCannotCommitASecondRow()
    {
        await EnsureOrdersAsync();
        var key = Key();
        var hash = Fingerprint(7);
        var invoked = 0;
        var executor = CreateExecutor(busyTimeoutSeconds: 2);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            try
            {
                var result = await executor.ExecuteAsync(key, hash,
                    async (db, ct) =>
                    {
                        Interlocked.Increment(ref invoked);
                        var order = new SqliteEfOrder { ScopeKeyHash = key, Quantity = 7 };
                        db.Orders.Add(order);
                        await db.SaveChangesAsync(ct);
                        return Response(order.Id, 7);
                    }, TimeSpan.FromHours(1));
                return (Result: result, Busy: false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                // A file-wide SQLITE_BUSY is a thrown storage failure, not
                // proof of a matching in-progress idempotency claim.
                return (Result: (TransactionalExecutionResult?)null, Busy: true);
            }
        }));
        Assert.Equal(1, invoked);
        Assert.Single(attempts, x => x.Result?.Status == TransactionalExecutionStatus.Executed);
        Assert.All(attempts, x => Assert.True(x.Busy ||
            x.Result?.Status == TransactionalExecutionStatus.Executed ||
            x.Result?.Status == TransactionalExecutionStatus.Replayed));
        Assert.Equal(1L, await CountAsync("orders_sqlite_ef_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
    }

    [Fact]
    public async Task CorruptProcessingRecordIsUncertainAndCannotRunEfCallback()
    {
        await EnsureOrdersAsync();
        var key = Key();
        var hash = Fingerprint(8);
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var claimed = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claimed.Status);
        await using (var conn = fixture.Connection())
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE idempotencykit_records SET lease_expires_at=NULL
                WHERE scope_key_hash=@key
                """;
            cmd.Parameters.AddWithValue("@key", key);
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await CreateExecutor().ExecuteAsync(key, hash,
            (db, ct) => throw new Exception("Unknown prior outcome cannot reexecute."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Uncertain, result.Status);
        Assert.Equal(0L, await CountAsync("orders_sqlite_ef_test", key));
    }
}
