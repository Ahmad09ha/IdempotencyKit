using System.Data;
using System.Net;
using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace IdempotencyKit.SqlServer.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable(
            "IDEMPOTENCYKIT_TEST_SQLSERVER")
            ?? throw new InvalidOperationException(
                "SQL Server integration connection string required. Run under sqlserver-ci.yml.");

        Exception? last = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(ConnectionString);
                await connection.OpenAsync();
                last = null;
                break;
            }
            catch (SqlException error)
            {
                last = error;
                await Task.Delay(1000);
            }
        }
        if (last is not null)
            throw new InvalidOperationException("SQL Server failed to start.", last);

        string? migration = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName,
                "spec", "sql", "sqlserver", "001_idempotencykit_records.sql");
            if (File.Exists(candidate))
            {
                migration = candidate;
                break;
            }
        }
        if (migration is null) throw new FileNotFoundException("SQL Server migration missing.");

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using (var schema = new SqlCommand(
            File.ReadAllText(migration), conn))
            await schema.ExecuteNonQueryAsync();

        await using (var orders = new SqlCommand("""
            IF OBJECT_ID(N'dbo.idempotencykit_sqlserver_orders_test', N'U') IS NULL
            EXEC(N'CREATE TABLE dbo.idempotencykit_sqlserver_orders_test
                (id bigint IDENTITY(1,1) PRIMARY KEY,
                 scope_key_hash nvarchar(67) NOT NULL,
                 quantity int NOT NULL)')
            """, conn))
            await orders.ExecuteNonQueryAsync();

        await using (var efOrders = new SqlCommand("""
            IF OBJECT_ID(N'dbo.idempotencykit_sqlserver_ef_orders_test', N'U') IS NULL
            EXEC(N'CREATE TABLE dbo.idempotencykit_sqlserver_ef_orders_test
                (id bigint IDENTITY(1,1) PRIMARY KEY,
                 scope_key_hash nvarchar(67) NOT NULL,
                 quantity int NOT NULL)')
            """, conn))
            await efOrders.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class SqlServerCoreTests(SqlServerFixture fixture)
    : IClassFixture<SqlServerFixture>
{
    private static string NewKey() => RequestIdentityHasher.ComputeScopeKey(
        new IdempotencyRequestScope("sqlserver-test", "orders.create.v1",
            "user-" + Guid.NewGuid().ToString("N"),
            "key-" + Guid.NewGuid().ToString("N"), "tenant-a"));

    private static string Fingerprint(int qty) => RequestFingerprinter.Compute(
        new RequestFingerprintInput("POST", "orders.create.v1", "/orders",
            BodyJson: $"{{\"quantity\":{qty}}}"));

    private static StoredHttpResponse Response(long id, int qty) =>
        new(201, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes($"{{\"id\":{id},\"quantity\":{qty}}}"),
            new Dictionary<string, string> { ["Location"] = "/orders/" + id });

    private async Task<long> CountAsync(string table, string key)
    {
        if (table is not ("idempotencykit_sqlserver_orders_test" or
                         "idempotencykit_records"))
            throw new ArgumentException("Unexpected test table.");
        await using var conn = new SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"SELECT COUNT_BIG(*) FROM dbo.{table} WHERE scope_key_hash = @key", conn);
        cmd.Parameters.AddWithValue("@key", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<StoredHttpResponse> InsertAsync(
        SqlConnection connection, SqlTransaction transaction,
        CancellationToken ct, string key, int qty)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO dbo.idempotencykit_sqlserver_orders_test(scope_key_hash, quantity)
            OUTPUT INSERTED.id VALUES (@key, @qty)
            """, connection, transaction);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@qty", qty);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return Response(id, qty);
    }

    [Fact]
    public async Task StandardModeClaimsFencesOwnersReplaysAndRejectsMismatches()
    {
        var store = new SqlServerIdempotencyStore(fixture.ConnectionString);
        string key = NewKey(), hash = Fingerprint(1);
        var first = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Acquired, first.Status);
        Assert.NotNull(first.OwnerToken);
        var concurrent = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.InProgress, concurrent.Status);
        Assert.False(await store.TryCompleteAsync(
            key, hash, Guid.NewGuid(), Response(42, 1), TimeSpan.FromHours(1)));
        Assert.False(await store.TryExtendLeaseAsync(
            key, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryExtendLeaseAsync(
            key, first.OwnerToken!.Value, TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryCompleteAsync(
            key, hash, first.OwnerToken.Value, Response(42, 1), TimeSpan.FromDays(90)));
        Assert.False(await store.TryCompleteAsync(
            key, hash, first.OwnerToken.Value, Response(43, 1), TimeSpan.FromDays(1)));
        var replay = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Completed, replay.Status);
        Assert.Equal(Response(42, 1).Body, replay.Response!.Body);
        Assert.Equal("/orders/42", replay.Response.Headers["location"]);
        var mismatch = await store.TryClaimAsync(key, Fingerprint(2),
            TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.FingerprintMismatch, mismatch.Status);
    }

    [Fact]
    public async Task StandardModeExpiredClaimBecomesUncertainNeverReclaimed()
    {
        var store = new SqlServerIdempotencyStore(fixture.ConnectionString);
        string key = NewKey(), hash = Fingerprint(2);
        var claim = await store.TryClaimAsync(
            key, hash, TimeSpan.FromMilliseconds(25));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claim.Status);
        await Task.Delay(120);
        var result = await store.TryClaimAsync(
            key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Uncertain, result.Status);
        Assert.False(await store.TryCompleteAsync(
            key, hash, claim.OwnerToken!.Value, Response(4, 2),
            TimeSpan.FromMinutes(1)));
        var after = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Uncertain, after.Status);
        Assert.False(await store.TryMarkUncertainAsync(
            key, hash, claim.OwnerToken.Value));
    }

    [Fact]
    public async Task TransactionCommitsDomainRowAndReplayResultTogether()
    {
        ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction> executor =
            new SqlServerTransactionalExecutor(fixture.ConnectionString);
        var key = NewKey();
        var hash = Fingerprint(3);
        int count = 0;
        var first = await executor.ExecuteAsync(key, hash, async (conn, tx, ct) =>
        {
            Interlocked.Increment(ref count);
            return await InsertAsync(conn, tx, ct, key, 3);
        }, TimeSpan.FromDays(90));
        Assert.Equal(TransactionalExecutionStatus.Executed, first.Status);
        Assert.Equal(1, count);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));

        var replay = await executor.ExecuteAsync(key, hash, (conn, tx, ct) =>
        {
            Interlocked.Increment(ref count);
            throw new Exception("Replay must not execute the domain callback.");
        }, TimeSpan.FromDays(1));
        Assert.Equal(TransactionalExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Response!.Body, replay.Response!.Body);
        Assert.Equal(first.Response.Headers["location"], replay.Response.Headers["location"]);
        Assert.Equal(1, count);

        var mismatch = await executor.ExecuteAsync(key, Fingerprint(4),
            (conn, tx, ct) => throw new Exception("Different body cannot execute."),
            TimeSpan.FromDays(1));
        Assert.Equal(TransactionalExecutionStatus.FingerprintMismatch, mismatch.Status);
        Assert.Null(mismatch.Response);

        var standard = new SqlServerIdempotencyStore(fixture.ConnectionString);
        var standardReplay = await standard.TryClaimAsync(
            key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Completed, standardReplay.Status);
        Assert.Equal(first.Response.Body, standardReplay.Response!.Body);
    }

    [Fact]
    public async Task DomainFailureAndInvalidResponseRollbackBusinessAndClaim()
    {
        var executor = new SqlServerTransactionalExecutor(fixture.ConnectionString);
        var key = NewKey();
        var hash = Fingerprint(5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            key, hash, async (conn, tx, ct) =>
            {
                await InsertAsync(conn, tx, ct, key, 5);
                throw new InvalidOperationException("Fault after order INSERT.");
            }, TimeSpan.FromHours(1)));
        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));

        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(
            key, hash, async (conn, tx, ct) =>
            {
                var response = await InsertAsync(conn, tx, ct, key, 5);
                return response with {
                    Headers = new Dictionary<string,string> {
                        ["Set-Cookie"] = "secret=unsafe" }
                };
            }, TimeSpan.FromHours(1)));
        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_orders_test", key));

        var success = await executor.ExecuteAsync(
            key, hash, (conn, tx, ct) => InsertAsync(conn, tx, ct, key, 5),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, success.Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
    }

    [Fact]
    public async Task HundredConcurrentSameKeyCallsCommitExactlyOneDomainWrite()
    {
        var executor = new SqlServerTransactionalExecutor(
            fixture.ConnectionString, TimeSpan.FromSeconds(8));
        var key = NewKey();
        var hash = Fingerprint(6);
        int called = 0;
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            executor.ExecuteAsync(key, hash, async (conn, tx, ct) =>
            {
                Interlocked.Increment(ref called);
                await Task.Delay(100, ct);
                return await InsertAsync(conn, tx, ct, key, 6);
            }, TimeSpan.FromHours(2))));

        Assert.Single(attempts, x => x.Status == TransactionalExecutionStatus.Executed);
        Assert.Equal(1, called);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
        Assert.All(attempts, x => Assert.Contains(x.Status, new[]
        {
            TransactionalExecutionStatus.Executed,
            TransactionalExecutionStatus.Replayed,
            TransactionalExecutionStatus.InProgress
        }));
    }

    [Fact]
    public async Task StandardUncertainAndProcessingPreventTransactionalExecution()
    {
        string key = NewKey(), hash = Fingerprint(7);
        var store = new SqlServerIdempotencyStore(fixture.ConnectionString);
        var executor = new SqlServerTransactionalExecutor(fixture.ConnectionString);
        var claim = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Acquired, claim.Status);
        var busy = await executor.ExecuteAsync(key, hash,
            (conn, tx, ct) => throw new Exception("Busy callback must not run."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.InProgress, busy.Status);

        Assert.True(await store.TryMarkUncertainAsync(
            key, hash, claim.OwnerToken!.Value));
        var uncertain = await executor.ExecuteAsync(key, hash,
            (conn, tx, ct) => throw new Exception("Unknown callback must not run."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Uncertain, uncertain.Status);
        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
    }

    [Fact]
    public async Task TransactionScopedKeyLockTimesOutWithoutCallback()
    {
        string key = NewKey(), hash = Fingerprint(8);
        var blocked = new SqlServerTransactionalExecutor(
            fixture.ConnectionString, TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var firstTask = blocked.ExecuteAsync(key, hash,
            async (conn, tx, ct) =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await gate.Task.WaitAsync(ct);
                return await InsertAsync(conn, tx, ct, key, 8);
            }, TimeSpan.FromHours(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = await blocked.ExecuteAsync(key, hash,
                (conn, tx, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    throw new Exception("Competing transaction must not execute.");
                }, TimeSpan.FromHours(1));
            Assert.Equal(TransactionalExecutionStatus.InProgress, second.Status);
            Assert.Equal(1, calls);
        }
        finally
        {
            gate.SetResult();
        }
        Assert.Equal(TransactionalExecutionStatus.Executed, (await firstTask).Status);
    }

    [Fact]
    public async Task CompletedCleanupNeverPurgesUncertainRecords()
    {
        var store = new SqlServerIdempotencyStore(fixture.ConnectionString);
        string oldKey = NewKey(), uncertainKey = NewKey();
        var old = await store.TryClaimAsync(oldKey, Fingerprint(9), TimeSpan.FromMinutes(1));
        Assert.True(await store.TryCompleteAsync(oldKey, Fingerprint(9),
            old.OwnerToken!.Value, Response(55, 9), TimeSpan.FromMilliseconds(1)));
        await Task.Delay(120);
        var other = await store.TryClaimAsync(
            uncertainKey, Fingerprint(10), TimeSpan.FromMinutes(1));
        Assert.True(await store.TryMarkUncertainAsync(
            uncertainKey, Fingerprint(10), other.OwnerToken!.Value));
        var removed = await store.PurgeExpiredCompletedAsync(100);
        Assert.True(removed >= 1);
        Assert.Equal(0L, await CountAsync("idempotencykit_records", oldKey));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", uncertainKey));
    }

    [Fact]
    public async Task DifferentKeysDoNotBlockOnOneAnothersApplicationLocks()
    {
        var heldKey = NewKey();
        var otherKey = NewKey();
        var executor = new SqlServerTransactionalExecutor(
            fixture.ConnectionString, TimeSpan.FromMilliseconds(150));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = executor.ExecuteAsync(heldKey, Fingerprint(20),
            async (conn, tx, ct) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
                return await InsertAsync(conn, tx, ct, heldKey, 20);
            }, TimeSpan.FromHours(1));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var independent = await executor.ExecuteAsync(
                otherKey, Fingerprint(21),
                (conn, tx, ct) => InsertAsync(conn, tx, ct, otherKey, 21),
                TimeSpan.FromHours(1));
            Assert.Equal(TransactionalExecutionStatus.Executed, independent.Status);
            // Don't scan the unindexed business table while the other INSERT
            // is uncommitted: a READ COMMITTED table scan could wait for that
            // unrelated row lock even though the key-specific app lock works.
        }
        finally
        {
            release.SetResult();
        }
        Assert.Equal(TransactionalExecutionStatus.Executed, (await held).Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", otherKey));
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", heldKey));
    }

    [Fact]
    public async Task KillingSqlServerSessionBeforeCommitRollsBackClaimAndOrder()
    {
        string key = NewKey(), hash = Fingerprint(22);
        var executor = new SqlServerTransactionalExecutor(fixture.ConnectionString);

        await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(
            key, hash, async (conn, tx, ct) =>
            {
                await InsertAsync(conn, tx, ct, key, 22);
                await using var spidCommand = new SqlCommand("SELECT @@SPID", conn, tx);
                var spid = (int)(await spidCommand.ExecuteScalarAsync(ct))!;

                // Independent admin connection kills this session while its
                // domain INSERT + idempotency reservation are uncommitted.
                // The command text contains only a trusted integer from SQL Server.
                await using var killer = new SqlConnection(fixture.ConnectionString);
                await killer.OpenAsync(ct);
                await using var kill = new SqlCommand("KILL " + spid, killer);
                await kill.ExecuteNonQueryAsync(ct);

                // Any further use of the dead transaction must fail.
                return Response(1, 22);
            }, TimeSpan.FromHours(1)));

        Assert.Equal(0L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
        Assert.Equal(0L, await CountAsync("idempotencykit_records", key));

        var retry = await executor.ExecuteAsync(
            key, hash, (conn, tx, ct) => InsertAsync(conn, tx, ct, key, 22),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, retry.Status);
        Assert.Equal(1L, await CountAsync("idempotencykit_sqlserver_orders_test", key));
        Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
    }


    [Fact]
    public async Task CorruptCompletedReplayHeadersAreRejectedBeforeExposure()
    {
        var store = new SqlServerIdempotencyStore(fixture.ConnectionString);
        string key = NewKey(), hash = Fingerprint(23);
        var claimed = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
        Assert.True(await store.TryCompleteAsync(
            key, hash, claimed.OwnerToken!.Value, Response(1, 23),
            TimeSpan.FromHours(1)));

        // Simulate a badly modified/compromised database row whose JSON is
        // syntactically valid, but contains an unsafe header.
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.idempotencykit_records
               SET response_headers = N'{"set-cookie":"token=secret"}'
             WHERE scope_key_hash = @key
            """, connection);
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1)));
    }

}
