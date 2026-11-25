using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IdempotencyKit.Sqlite.Tests;

public sealed class FileSqliteFixture : IAsyncLifetime
{
    public string DirectoryPath { get; } = Path.Combine(
        Path.GetTempPath(), "idempotencykit-sqlite-" + Guid.NewGuid().ToString("N"));
    public string DatabasePath => Path.Combine(DirectoryPath, "orders.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        await SqliteIdempotencyDatabase.InitializeAsync(DatabasePath);
        await using var conn = Connection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS orders_sqlite_test (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              scope_key_hash TEXT NOT NULL,
              quantity INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public SqliteConnection Connection() => new(
        new SqliteConnectionStringBuilder {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1 }.ToString());

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DirectoryPath, true); }
        catch (IOException) { /* Windows pending file handle after test; CI is Linux. */ }
        return Task.CompletedTask;
    }
}

public sealed class SqliteCoreTests(FileSqliteFixture fixture)
    : IClassFixture<FileSqliteFixture>
{
    private static string NewKey() => RequestIdentityHasher.ComputeScopeKey(
        new IdempotencyRequestScope("sqlite-dotnet-test","orders.create.v1",
            "principal", "key-" + Guid.NewGuid().ToString("N"),"tenant"));

    private static string Fingerprint(int qty) => RequestFingerprinter.Compute(
        new RequestFingerprintInput("POST","orders.create.v1","/orders",
            BodyJson: $"{{\"quantity\":{qty}}}"));

    private static StoredHttpResponse Response(long id, int qty) => new(
        201, "application/json; charset=utf-8",
        Encoding.UTF8.GetBytes($"{{\"id\":{id},\"quantity\":{qty}}}"),
        new Dictionary<string, string> { ["Location"] = "/orders/" + id });

    private async Task<long> CountAsync(string table, string key)
    {
        if (table is not ("orders_sqlite_test" or "idempotencykit_records"))
            throw new ArgumentException("Unknown table.");
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT COUNT(*) FROM {table} WHERE scope_key_hash = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<StoredHttpResponse> InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken, string key, int qty)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO orders_sqlite_test(scope_key_hash,quantity)
                VALUES (@key,@qty) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@qty", qty);
        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken))!;
        return Response(id, qty);
    }

    [Fact]
    public async Task FileBackedInitializationEnablesWalFullAndSchema()
    {
        Assert.True(File.Exists(fixture.DatabasePath));
        await using var conn = fixture.Connection();
        await conn.OpenAsync();
        await using var wal = conn.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string?)await wal.ExecuteScalarAsync());
        wal.CommandText = "PRAGMA synchronous=FULL";
        await wal.ExecuteNonQueryAsync();
        wal.CommandText = "PRAGMA synchronous";
        Assert.Equal(2L, (long)(await wal.ExecuteScalarAsync())!);
        Assert.Throws<ArgumentException>(() =>
            new SqliteIdempotencyStore(":memory:"));
        Assert.Throws<ArgumentException>(() =>
            new SqliteTransactionalExecutor("file:memory:?cache=shared"));
    }

    [Fact]
    public async Task StandardClaimOwnerFencingLeaseReplayAndMismatch()
    {
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var key = NewKey();
        var fingerprint = Fingerprint(1);
        var first = await store.TryClaimAsync(key,fingerprint,TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Acquired,first.Status);
        Assert.NotNull(first.OwnerToken);
        Assert.Equal(IdempotencyClaimStatus.InProgress,(await store.TryClaimAsync(
            key,fingerprint,TimeSpan.FromMinutes(1))).Status);

        Assert.False(await store.TryCompleteAsync(key,fingerprint,
            Guid.NewGuid(), Response(3,1),TimeSpan.FromHours(1)));
        Assert.False(await store.TryExtendLeaseAsync(
            key,Guid.NewGuid(),TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryExtendLeaseAsync(
            key,first.OwnerToken!.Value,TimeSpan.FromMinutes(1)));
        Assert.True(await store.TryCompleteAsync(key,fingerprint,
            first.OwnerToken.Value,Response(3,1),TimeSpan.FromHours(1)));
        Assert.False(await store.TryCompleteAsync(key,fingerprint,
            first.OwnerToken.Value,Response(4,1),TimeSpan.FromHours(1)));

        // Open a NEW store with a NEW physical SQLite connection; persisted
        // response must survive closing/reopening the database file.
        var other = new SqliteIdempotencyStore(fixture.DatabasePath);
        var replay = await other.TryClaimAsync(
            key,fingerprint,TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Completed,replay.Status);
        Assert.Equal(Response(3,1).Body,replay.Response!.Body);
        Assert.Equal("/orders/3",replay.Response.Headers["location"]);
        Assert.Equal(IdempotencyClaimStatus.FingerprintMismatch,(await other.TryClaimAsync(
            key,Fingerprint(2),TimeSpan.FromMinutes(1))).Status);
    }

    [Fact]
    public async Task ExpiredStandardClaimMovesToUncertainNotAutomaticRetry()
    {
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var key = NewKey();
        var claimed = await store.TryClaimAsync(
            key,Fingerprint(2),TimeSpan.FromMilliseconds(20));
        await Task.Delay(110);
        Assert.Equal(IdempotencyClaimStatus.Uncertain,(await store.TryClaimAsync(
            key,Fingerprint(2),TimeSpan.FromMinutes(1))).Status);
        Assert.False(await store.TryCompleteAsync(key,Fingerprint(2),
            claimed.OwnerToken!.Value,Response(1,2),TimeSpan.FromDays(1)));
        Assert.Equal(IdempotencyClaimStatus.Uncertain,(await store.TryClaimAsync(
            key,Fingerprint(2),TimeSpan.FromDays(1))).Status);
        Assert.False(await store.TryMarkUncertainAsync(
            key,Fingerprint(2),claimed.OwnerToken.Value));
    }

    [Fact]
    public async Task TransactionalCommitMakesOrderAndReplayAtomic()
    {
        ITransactionalIdempotencyExecutor<SqliteConnection,SqliteTransaction> executor =
            new SqliteTransactionalExecutor(fixture.DatabasePath);
        var key = NewKey();
        var fingerprint = Fingerprint(3);
        int calls = 0;
        var executed = await executor.ExecuteAsync(key,fingerprint,
            async (conn,tx,ct) => {
                Interlocked.Increment(ref calls);
                return await InsertAsync(conn,tx,ct,key,3);
            },TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed,executed.Status);
        Assert.Equal(1L,await CountAsync("orders_sqlite_test",key));
        Assert.Equal(1L,await CountAsync("idempotencykit_records",key));

        var replay = await new SqliteTransactionalExecutor(fixture.DatabasePath)
            .ExecuteAsync(key,fingerprint,
                (conn,tx,ct) => throw new InvalidOperationException(
                    "Replayed operation cannot run callback."),TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Replayed,replay.Status);
        Assert.Equal(executed.Response!.Body,replay.Response!.Body);
        Assert.Equal(executed.Response.Headers["location"],replay.Response.Headers["location"]);
        Assert.Equal(1,calls);

        var standardReplay = await new SqliteIdempotencyStore(fixture.DatabasePath)
            .TryClaimAsync(key,fingerprint,TimeSpan.FromMinutes(1));
        Assert.Equal(IdempotencyClaimStatus.Completed,standardReplay.Status);
        Assert.Equal(executed.Response.Body,standardReplay.Response!.Body);

        var mismatch = await executor.ExecuteAsync(key,Fingerprint(4),
            (conn,tx,ct) => throw new Exception("Mismatch cannot run."),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.FingerprintMismatch,mismatch.Status);
    }

    [Fact]
    public async Task ThrowAndInvalidCaptureRollBackBothTables()
    {
        var executor = new SqliteTransactionalExecutor(fixture.DatabasePath);
        var key = NewKey(); var hash = Fingerprint(5);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(key,hash,async (conn,tx,ct) => {
                await InsertAsync(conn,tx,ct,key,5);
                throw new InvalidOperationException("Fail after insert.");
            },TimeSpan.FromHours(1)));
        Assert.Equal(0L,await CountAsync("orders_sqlite_test",key));
        Assert.Equal(0L,await CountAsync("idempotencykit_records",key));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            executor.ExecuteAsync(key,hash,async (conn,tx,ct) => {
                await InsertAsync(conn,tx,ct,key,5);
                return Response(1,5) with {
                    Headers = new Dictionary<string,string> {
                        ["Set-Cookie"] = "secret=unsafe" }
                };
            },TimeSpan.FromHours(1)));
        Assert.Equal(0L,await CountAsync("orders_sqlite_test",key));
        Assert.Equal(0L,await CountAsync("idempotencykit_records",key));
        var success = await executor.ExecuteAsync(key,hash,
            (conn,tx,ct) => InsertAsync(conn,tx,ct,key,5),
            TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed,success.Status);
        Assert.Equal(1L,await CountAsync("orders_sqlite_test",key));
    }

    [Fact]
    public async Task OneHundredSameKeyCallsNeverWriteTwice()
    {
        var key=NewKey();
        var fingerprint=Fingerprint(6);
        var executor=new SqliteTransactionalExecutor(fixture.DatabasePath, 5);
        int calls=0;
        var attempts=await Task.WhenAll(Enumerable.Range(0,100).Select(async _ => {
            try {
                var result = await executor.ExecuteAsync(key,fingerprint,
                    async (conn,tx,ct) => {
                        Interlocked.Increment(ref calls);
                        await Task.Delay(12,ct);
                        return await InsertAsync(conn,tx,ct,key,6);
                    },TimeSpan.FromHours(1));
                return (Result: result, StorageBusy: false);
            } catch (SqliteException busy) when (busy.SqliteErrorCode is 5 or 6) {
                // Track a genuine thrown storage exception SEPARATELY from
                // transactional status. Never fabricate an Uncertain/InProgress
                // outcome just to make a busy writer look like a duplicate.
                return (Result: (TransactionalExecutionResult?)null, StorageBusy: true);
            }
        }));
        Assert.Equal(1,calls);
        Assert.Single(attempts,x=>x.Result?.Status==TransactionalExecutionStatus.Executed);
        Assert.Equal(1L,await CountAsync("orders_sqlite_test",key));
        Assert.Equal(1L,await CountAsync("idempotencykit_records",key));
        Assert.All(attempts,x=>Assert.True(x.StorageBusy ||
            x.Result?.Status is TransactionalExecutionStatus.Executed
                or TransactionalExecutionStatus.Replayed));
    }

    [Fact]
    public async Task UnrelatedWriterBusyThrowsStorageErrorRatherThanInProgress()
    {
        // Hold a transaction writing a totally unrelated table/key in one
        // process; SQLite's SINGLE writer is file-wide, not key-specific.
        await using var blocker=fixture.Connection();
        await blocker.OpenAsync();
        await using var tx=blocker.BeginTransaction(deferred:false);
        await using var held=blocker.CreateCommand();
        held.Transaction=tx;
        held.CommandText="INSERT INTO orders_sqlite_test(scope_key_hash,quantity) VALUES ('unrelated-lock',1)";
        await held.ExecuteNonQueryAsync();
        var store = new SqliteIdempotencyStore(fixture.DatabasePath,1);
        var key=NewKey();
        var error=await Assert.ThrowsAsync<SqliteException>(()=>
            store.TryClaimAsync(key,Fingerprint(9),TimeSpan.FromSeconds(2)));
        Assert.Contains(error.SqliteErrorCode,new[]{5,6});
        Assert.Equal(0L,await CountAsync("idempotencykit_records",key));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task CleanupPurgesOnlyExpiredCompletedButNeverUncertain()
    {
        var store=new SqliteIdempotencyStore(fixture.DatabasePath);
        var expiredKey=NewKey();var unknownKey=NewKey();
        var old=await store.TryClaimAsync(
            expiredKey,Fingerprint(11),TimeSpan.FromMinutes(1));
        Assert.True(await store.TryCompleteAsync(
            expiredKey,Fingerprint(11),old.OwnerToken!.Value,
            Response(1,11),TimeSpan.FromMilliseconds(1)));
        var unknown=await store.TryClaimAsync(
            unknownKey,Fingerprint(12),TimeSpan.FromMinutes(1));
        Assert.True(await store.TryMarkUncertainAsync(
            unknownKey,Fingerprint(12),unknown.OwnerToken!.Value));
        await Task.Delay(90);
        Assert.True(await store.PurgeExpiredCompletedAsync(100)>0);
        Assert.Equal(0L,await CountAsync("idempotencykit_records",expiredKey));
        Assert.Equal(1L,await CountAsync("idempotencykit_records",unknownKey));
    }

    [Fact]
    public async Task CorruptedStoredReplayIsRejectedBeforeExposure()
    {
        var store=new SqliteIdempotencyStore(fixture.DatabasePath);
        var key=NewKey();var hash=Fingerprint(13);
        var claim=await store.TryClaimAsync(key,hash,TimeSpan.FromHours(1));
        Assert.True(await store.TryCompleteAsync(key,hash,claim.OwnerToken!.Value,
            Response(1,13),TimeSpan.FromHours(1)));
        await using var conn=fixture.Connection();
        await conn.OpenAsync();
        await using var cmd=conn.CreateCommand();
        cmd.CommandText="UPDATE idempotencykit_records SET response_headers=@headers WHERE scope_key_hash=@key";
        cmd.Parameters.AddWithValue("@key",key);
        cmd.Parameters.AddWithValue("@headers","{\"set-cookie\":\"secret=1\"}");
        await cmd.ExecuteNonQueryAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>
            store.TryClaimAsync(key,hash,TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task CorruptProcessingRecordWithMissingLeaseFailsClosedAsUncertain()
    {
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var executor = new SqliteTransactionalExecutor(fixture.DatabasePath);
        for (var n = 0; n < 2; n++)
        {
            var key = NewKey();
            var hash = Fingerprint(30 + n);
            var claimed = await store.TryClaimAsync(
                key, hash, TimeSpan.FromMinutes(1));
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

            if (n == 0)
                Assert.Equal(IdempotencyClaimStatus.Uncertain,
                    (await store.TryClaimAsync(key, hash,
                        TimeSpan.FromMinutes(1))).Status);
            else
                Assert.Equal(TransactionalExecutionStatus.Uncertain,
                    (await executor.ExecuteAsync(key, hash,
                        (conn, tx, ct) => throw new Exception(
                            "Corrupt processing record cannot run business SQL."),
                        TimeSpan.FromMinutes(1))).Status);
        }
    }


    [Fact]
    public async Task UnsafeReplayFieldsAndInvalidUtf8CannotCommitOrReplay()
    {
        var store = new SqliteIdempotencyStore(fixture.DatabasePath);
        var executor = new SqliteTransactionalExecutor(fixture.DatabasePath);
        var valid = Response(1, 2);
        var invalid = new[]
        {
            valid with { StatusCode = 204 },
            valid with { StatusCode = 205 },
            valid with { StatusCode = 206 },
            valid with { Headers = new Dictionary<string, string> { ["Location"] = "/orders/\u0627" } },
            valid with { Headers = new Dictionary<string, string> { ["ETag"] = "\"caf\u00e9\"" } },
            valid with { ContentType = "application/json; x=\u00e9" },
            valid with { Headers = new Dictionary<string, string> { ["Location"] = "/orders/\u0000bad" } },
            valid with { Headers = new Dictionary<string, string> { ["ETag"] = "bad\u0001value" } },
            valid with { ContentType = "application/json; charset=utf-8\u007f" },
            valid with { Body = new byte[] { 0x22, 0xff, 0x22 } },
            valid with { Body = Encoding.UTF8.GetBytes("not-json") }
        };
        foreach (var bad in invalid)
        {
            var key = NewKey();
            var hash = Fingerprint(2);
            var claimed = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
            Assert.Equal(IdempotencyClaimStatus.Acquired, claimed.Status);
            await Assert.ThrowsAnyAsync<Exception>(() => store.TryCompleteAsync(
                key, hash, claimed.OwnerToken!.Value, bad, TimeSpan.FromHours(1)));
            Assert.Equal(IdempotencyClaimStatus.InProgress,
                (await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1))).Status);
            Assert.True(await store.TryCompleteAsync(key, hash, claimed.OwnerToken!.Value,
                valid, TimeSpan.FromHours(1)));

            var txKey = NewKey();
            var called = false;
            await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(
                txKey, hash, async (connection, transaction, ct) =>
                {
                    // The business SQL must really run before capture fails.
                    await InsertAsync(connection, transaction, ct, txKey, 2);
                    called = true;
                    return bad;
                }, TimeSpan.FromHours(1)));
            Assert.True(called);
            Assert.Equal(0L, await CountAsync("orders_sqlite_test", txKey));
            Assert.Equal(0L, await CountAsync("idempotencykit_records", txKey));
        }

        // Directly corrupt completed test rows without bypassing the schema.
        // Both store and transactional replay must reject them; no callback
        // may run and the record must remain available for reconciliation.
        var tampered = new (string Headers, byte[] Body, string Type, int Status)[]
        {
            (Headers: "{\"set-cookie\":\"fixture\"}", Body: valid.Body, Type: valid.ContentType, Status: 201),
            (Headers: "{\"location\":\"/orders/\\u0627\"}", Body: valid.Body, Type: valid.ContentType, Status: 201),
            (Headers: "{\"location\":\"bad\\u0001value\"}", Body: valid.Body, Type: valid.ContentType, Status: 201),
            (Headers: "{}", Body: valid.Body, Type: "text/html", Status: 201),
            (Headers: "{}", Body: new byte[] { 0x22, 0xff, 0x22 }, Type: valid.ContentType, Status: 201),
            (Headers: "{}", Body: Encoding.UTF8.GetBytes("not-json"), Type: valid.ContentType, Status: 201)
        };
        tampered = [.. tampered, ("{}", valid.Body, valid.ContentType, 204),
            ("{}", valid.Body, valid.ContentType, 205), ("{}", valid.Body, valid.ContentType, 206)];
        foreach (var bad in tampered)
        {
            var key = NewKey();
            var hash = Fingerprint(2);
            var claim = await store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1));
            Assert.True(await store.TryCompleteAsync(key, hash, claim.OwnerToken!.Value,
                valid, TimeSpan.FromHours(1)));
            await using (var connection = fixture.Connection())
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE idempotencykit_records SET response_status=@status, response_headers=@headers, response_body=@body, response_content_type=@type WHERE scope_key_hash=@key";
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("status", bad.Status);
                command.Parameters.AddWithValue("headers", bad.Headers);
                command.Parameters.AddWithValue("body", bad.Body);
                command.Parameters.AddWithValue("type", bad.Type);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            await Assert.ThrowsAnyAsync<Exception>(() =>
                store.TryClaimAsync(key, hash, TimeSpan.FromMinutes(1)));
            var ran = false;
            await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(key, hash,
                (_, _, _) =>
                {
                    ran = true;
                    return Task.FromResult(valid);
                }, TimeSpan.FromHours(1)));
            Assert.False(ran);
            Assert.Equal(1L, await CountAsync("idempotencykit_records", key));
            Assert.Equal(0L, await CountAsync("orders_sqlite_test", key));
        }
    }

}
