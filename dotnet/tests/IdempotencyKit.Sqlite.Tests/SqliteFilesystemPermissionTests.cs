using System.Net;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Sqlite;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IdempotencyKit.Sqlite.Tests;

// The POSIX fault is real only under an unprivileged Linux account.
public sealed class UnprivilegedLinuxFactAttribute : FactAttribute
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    public UnprivilegedLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || EffectiveUserId() == 0)
            Skip = "Requires an unprivileged Linux user to exercise actual filesystem permission denial.";
    }
}

public sealed class SqliteFilesystemPermissionTests
{
    [UnprivilegedLinuxFact]
    public async Task PermissionLossFailsClosedAndRestoringPermissionsPreservesSameKeyRecovery()
    {
        var folder = Directory.CreateTempSubdirectory("idk-permission-fault-");
        var file = Path.Combine(folder.FullName, "orders.db");
        int callbacks = 0;
        try
        {
            await SqliteIdempotencyDatabase.InitializeAsync(file);
            await using (var db = Connection(file))
            {
                await db.OpenAsync();
                await using var ddl = db.CreateCommand();
                ddl.CommandText = "CREATE TABLE orders (id TEXT PRIMARY KEY, quantity INTEGER NOT NULL)";
                await ddl.ExecuteNonQueryAsync();
            }
            using var server = new TestServer(new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, SqliteTestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSqliteTransactionalIdempotency(file, 1);
                    services.AddIdempotencyKit(options =>
                    {
                        options.Namespace = "sqlite-permission-lab";
                        options.ResolveCaller = http => new IdempotencyCallerScope(
                            http.User.FindFirst(ClaimTypes.NameIdentifier)!.Value, "fixture-tenant");
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(routes => routes.MapSqliteTransactionalPost(
                        "/orders", "create-order", async (_, _, db, tx, ct) =>
                        {
                            Interlocked.Increment(ref callbacks);
                            var id = Guid.NewGuid().ToString("N");
                            await using var insert = db.CreateCommand();
                            insert.Transaction = tx;
                            insert.CommandText = "INSERT INTO orders VALUES (@id, 2)";
                            insert.Parameters.AddWithValue("@id", id);
                            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
                            return new StoredHttpResponse(201, "application/json",
                                JsonSerializer.SerializeToUtf8Bytes(new { id, quantity = 2 }),
                                new Dictionary<string, string> { ["Location"] = "/orders/" + id });
                        }));
                }));
            using var client = server.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var original = await SendAsync(client, "already-completed");
            Assert.Equal(HttpStatusCode.Created, original.StatusCode);
            var saved = await original.Content.ReadAsByteArrayAsync();
            Assert.Equal((1L, 1L), await CountsAsync(file));

            // Connections are closed between requests, so the next operation must
            // actually access the protected local file and create/open WAL files.
            // Change only the disposable fixture's permissions; always restore them.
            var originalDirectoryMode = File.GetUnixFileMode(folder.FullName);
            var modes = Directory.GetFiles(folder.FullName)
                .ToDictionary(path => path, File.GetUnixFileMode);
            try
            {
                foreach (var path in modes.Keys) File.SetUnixFileMode(path, UnixFileMode.UserRead);
                File.SetUnixFileMode(folder.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.Throws<UnauthorizedAccessException>(() =>
                {
                    using var probe = File.Open(file, FileMode.Open, FileAccess.Write);
                });
                Assert.Throws<UnauthorizedAccessException>(() =>
                {
                    using var probe = File.Create(Path.Combine(folder.FullName, "write-probe"));
                });

                using var failed = await SendAsync(client, "new-operation");
                await AssertUnavailable(failed);
                using var unavailableReplay = await SendAsync(client, "already-completed");
                await AssertUnavailable(unavailableReplay);
                Assert.Equal(1, callbacks);
            }
            finally
            {
                File.SetUnixFileMode(folder.FullName, originalDirectoryMode);
                foreach (var (path, mode) in modes) File.SetUnixFileMode(path, mode);
            }

            // Read both committed tables independently after restoring access.
            Assert.Equal((1L, 1L), await CountsAsync(file));
            using var oldRetry = await SendAsync(client, "already-completed");
            Assert.Equal(HttpStatusCode.Created, oldRetry.StatusCode);
            Assert.Equal(saved, await oldRetry.Content.ReadAsByteArrayAsync());
            Assert.Equal(original.Headers.Location, oldRetry.Headers.Location);
            Assert.Equal(1, callbacks);

            using var retry = await SendAsync(client, "new-operation");
            Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
            var newBytes = await retry.Content.ReadAsByteArrayAsync();
            using var replay = await SendAsync(client, "new-operation");
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(newBytes, await replay.Content.ReadAsByteArrayAsync());
            Assert.Equal(retry.Headers.Location, replay.Headers.Location);
            Assert.Equal((2L, 2L), await CountsAsync(file));
            Assert.Equal(2, callbacks);
        }
        finally { folder.Delete(recursive: true); }
    }

    private static async Task AssertUnavailable(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("IDEMPOTENCY_TRANSACTION_UNAVAILABLE", json.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent("{\"quantity\":2}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Test-User", "fixture-user");
        return await client.SendAsync(request);
    }

    private static SqliteConnection Connection(string file) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1
        }.ToString());

    private static async Task<(long Orders, long Records)> CountsAsync(string file)
    {
        await using var db = Connection(file);
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM orders), (SELECT COUNT(*) FROM idempotencykit_records WHERE state='completed')";
        await using var rows = await cmd.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        var result = (rows.GetInt64(0), rows.GetInt64(1));
        await rows.DisposeAsync();
        cmd.CommandText = "SELECT COUNT(*) FROM idempotencykit_records";
        Assert.Equal(result.Item2, (long)(await cmd.ExecuteScalarAsync())!);
        return result;
    }
}
