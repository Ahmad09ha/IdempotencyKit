using System.Text;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdempotencyKit.Sqlite.Tests;

public sealed class SqliteCapacityFailureTests(FileSqliteFixture fixture)
    : IClassFixture<FileSqliteFixture>
{
    [Fact]
    public async Task BoundedPageCapacityFailureRollsBackClaimAndBusinessInsert()
    {
        // Exercise real SQLite's SQLITE_FULL without consuming machine disk:
        // only this transaction connection is subject to the page capacity.
        var scope = RequestIdentityHasher.ComputeScopeKey(new IdempotencyRequestScope(
            "sqlite-capacity", "orders.create.v1", "alice", "order-full", "acme"));
        var fingerprint = RequestFingerprinter.Compute(new RequestFingerprintInput(
            "POST", "orders.create.v1", "/orders", BodyJson: "{}"));
        var executor = new SqliteTransactionalExecutor(fixture.DatabasePath);

        var full = await Assert.ThrowsAsync<SqliteException>(() => executor.ExecuteAsync(
            scope, fingerprint, async (connection, transaction, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO orders_sqlite_test(scope_key_hash, quantity) VALUES (@key,1);
                    """;
                command.Parameters.AddWithValue("@key", scope);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));

                command.CommandText = "PRAGMA page_count";
                var pages = (long)(await command.ExecuteScalarAsync(ct))!;
                command.CommandText = FormattableString.Invariant($"PRAGMA max_page_count={pages}");
                Assert.Equal(pages, (long)(await command.ExecuteScalarAsync(ct))!);

                // This BLOB needs many fresh pages; the application data,
                // claim and replay are in the same transaction.
                command.CommandText = """
                    UPDATE orders_sqlite_test SET quantity=zeroblob(1048576)
                    WHERE scope_key_hash=@key;
                    """;
                await command.ExecuteNonQueryAsync(ct);
                throw new InvalidOperationException("SQLite capacity fixture did not produce SQLITE_FULL.");
            }, TimeSpan.FromHours(1)));

        Assert.Equal(13, full.SqliteErrorCode); // SQLITE_FULL
        Assert.Equal((0L, 0L), await PersistedCounts(scope));

        // A new healthy connection can retry the same key, write once, and
        // store a completed result. Do not retry automatically inside the executor.
        var retry = await executor.ExecuteAsync(scope, fingerprint, async (connection, transaction, ct) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO orders_sqlite_test(scope_key_hash, quantity) VALUES (@key,1);
                """;
            command.Parameters.AddWithValue("@key", scope);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
            return new StoredHttpResponse(201, "application/json", Encoding.UTF8.GetBytes("{}"),
                new Dictionary<string, string>());
        }, TimeSpan.FromHours(1));
        Assert.Equal(TransactionalExecutionStatus.Executed, retry.Status);
        Assert.Equal((1L, 1L), await PersistedCounts(scope));
    }

    private async Task<(long Orders, long Claims)> PersistedCounts(string key)
    {
        await using var connection = fixture.Connection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM orders_sqlite_test WHERE scope_key_hash=@key),
                   (SELECT COUNT(*) FROM idempotencykit_records WHERE scope_key_hash=@key);
            """;
        command.Parameters.AddWithValue("@key", key);
        await using var rows = await command.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        return (rows.GetInt64(0), rows.GetInt64(1));
    }
}
