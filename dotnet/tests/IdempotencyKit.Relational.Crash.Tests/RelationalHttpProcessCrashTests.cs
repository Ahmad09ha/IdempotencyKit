using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IdempotencyKit.Core;
using IdempotencyKit.Relational.CrashHost;
using Microsoft.Data.SqlClient;
using Npgsql;
using Xunit;
using static IdempotencyKit.Relational.CrashHost.RelationalCrashFixture;

namespace IdempotencyKit.Relational.Crash.Tests;

public sealed class RelationalHttpProcessCrashTests
{
    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task KilledKestrelProcessPreservesAtomicStateAndSameKeyRecovery(string stage)
    {
        var provider = Environment.GetEnvironmentVariable("IDEMPOTENCYKIT_CRASH_PROVIDER")
            ?? throw new InvalidOperationException("Choose postgres or sqlserver in the dedicated crash CI.");
        Assert.Contains(provider, new[] { "postgres", "sqlserver" });
        var source = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException("A dedicated disposable test DB is required.");
        // The test never runs against a caller's application database. SQL Server gets a
        // newly created test database; PostgreSQL requires the dedicated idk_crash DB.
        var connectionString = await PrepareDatabaseAsync(provider, source);
        var key = "crash-" + Guid.NewGuid().ToString("N");
        var scope = RequestIdentityHasher.ComputeScopeKey(
            new IdempotencyRequestScope(Namespace, Operation, Subject, key, Tenant));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var ct = timeout.Token;
        try
        {
            using var first = StartHost(provider, connectionString, stage);
            var url = await WaitForSignalAsync(first, ListeningSignal, ct);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { Timeout = Timeout.InfiniteTimeSpan };
            using var initial = Request(url, key);
            var pending = client.SendAsync(initial, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(stage, await WaitForSignalAsync(first, BlockedSignal, ct));
            Assert.False(pending.IsCompleted, "The client must not have response headers.");

            // PostgreSQL READ COMMITTED and SQL Server RCSI expose committed state without
            // waiting on the blocked writer. Never use dirty reads / NOLOCK as evidence.
            bool committed = stage == "after-commit";
            var beforeKill = await ReadStateAsync(provider, connectionString, key, scope, ct);
            AssertAtomic(beforeKill, committed);
            Assert.False(pending.IsCompleted, "The response must still be withheld.");

            first.Process.Kill();
            await first.Process.WaitForExitAsync(ct);
            Assert.NotEqual(0, first.Process.ExitCode);
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var unexpected = await pending;
            });

            var afterKill = await ReadStateAsync(provider, connectionString, key, scope, ct);
            AssertAtomic(afterKill, committed);
            Assert.Equal(beforeKill.OrderId, afterKill.OrderId);
            Assert.Equal(beforeKill.Body, afterKill.Body);

            using var retry = StartHost(provider, connectionString, "retry");
            var retryUrl = await WaitForSignalAsync(retry, ListeningSignal, ct);

            // With RCSI an observer can see no committed row while server-side
            // rollback is still finishing. Bound same-key 409 retries, never mint a new key.
            using var response = await RetryAsync(client, retryUrl, key, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var json = JsonDocument.Parse(bytes);
            var id = json.RootElement.GetProperty("id").GetString();
            Assert.NotNull(id);
            Assert.Equal(2, json.RootElement.GetProperty("quantity").GetInt32());
            Assert.Equal(Route + "/" + id, response.Headers.Location?.OriginalString);

            var final = await ReadStateAsync(provider, connectionString, key, scope, ct);
            AssertAtomic(final, true);
            Assert.Equal(id, final.OrderId);
            Assert.Equal(bytes, final.Body);
            if (committed)
            {
                Assert.Equal(beforeKill.OrderId, id);
                Assert.Equal(beforeKill.Body, bytes);
            }

            // Exercise a second same-key replay and a changed request on the surviving host.
            using var replayRequest = Request(retryUrl, key);
            using var replay = await client.SendAsync(replayRequest, ct);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(bytes, await replay.Content.ReadAsByteArrayAsync(ct));
            Assert.Equal(response.Headers.Location, replay.Headers.Location);
            using var changedRequest = Request(retryUrl, key, 3);
            using var changed = await client.SendAsync(changedRequest, ct);
            Assert.Equal((HttpStatusCode)422, changed.StatusCode);
            using var unauthorizedRequest = Request(retryUrl, key, authenticate: false);
            using var unauthorized = await client.SendAsync(unauthorizedRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            var finalAgain = await ReadStateAsync(provider, connectionString, key, scope, ct);
            AssertAtomic(finalAgain, true);
            Assert.Equal(id, finalAgain.OrderId);
            Assert.Equal(bytes, finalAgain.Body);
        }
        finally
        {
            // PostgreSQL CI owns an ephemeral service. SQL Server's dedicated randomly
            // named database is dropped only after both child processes have been disposed.
            if (provider == "sqlserver")
                await DropSqlServerDatabaseAsync(source, connectionString);
        }
    }

    private static async Task<HttpResponseMessage> RetryAsync(
        HttpClient client, string url, string key, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            using var request = Request(url, key);
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Conflict || i == 19)
                return response;
            // A committed uncertain record is never expected in this transaction-only test.
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!body.Contains("IDEMPOTENCY_IN_PROGRESS", StringComparison.Ordinal))
                return response;
            response.Dispose();
            await Task.Delay(250, ct);
        }
        throw new InvalidOperationException("Unreachable retry loop.");
    }

    private static void AssertAtomic(DatabaseState state, bool committed)
    {
        Assert.Equal(committed ? 1L : 0L, state.Orders);
        Assert.Equal(committed ? 1L : 0L, state.Records);
        Assert.Equal(committed ? "completed" : null, state.RecordState);
        Assert.Equal(committed, state.OrderId is not null);
        Assert.Equal(committed, state.Body is { Length: > 0 });
    }

    private static HttpRequestMessage Request(string url, string key, int quantity = 2, bool authenticate = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url + Route)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { quantity }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        if (authenticate) request.Headers.Add(AuthenticationHeader, Subject);
        return request;
    }

    private static HostProcess StartHost(string provider, string connectionString, string stage)
    {
        var runtimeDotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet))
            dotnet = File.Exists(runtimeDotnet) ? runtimeDotnet : "dotnet";
        var assembly = typeof(RelationalHttpProcessCrashTests).Assembly.Location;
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment[ConnectionVariable] = connectionString;
        foreach (var arg in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"),
            typeof(RelationalCrashFixture).Assembly.Location, provider, stage
        }) start.ArgumentList.Add(arg);
        return new HostProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Unable to launch Kestrel crash fixture."));
    }

    private static async Task<string> WaitForSignalAsync(HostProcess host, string prefix, CancellationToken ct)
    {
        var lines = new List<string>();
        try
        {
            while (await host.Process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
                if (lines.Count < 20) lines.Add(line);
            }
        }
        catch (OperationCanceledException e)
        {
            throw new TimeoutException("Crash host did not emit " + prefix, e);
        }
        throw new InvalidOperationException("Crash host exited before " + prefix +
            ": " + string.Join(Environment.NewLine, lines) + "\n" + await host.Errors.WaitAsync(ct));
    }

    private static DbConnection Connection(string provider, string connectionString)
        => provider == "postgres" ? new NpgsqlConnection(connectionString) : new SqlConnection(connectionString);

    private static async Task<string> PrepareDatabaseAsync(string provider, string source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = timeout.Token;
        string connectionString = source;
        if (provider == "postgres")
        {
            var pg = new NpgsqlConnectionStringBuilder(source);
            if (pg.Database != "idk_crash")
                throw new InvalidOperationException("Use only the dedicated PostgreSQL idk_crash database.");
        }
        else
        {
            var sql = new SqlConnectionStringBuilder(source);
            if (!string.Equals(sql.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use the disposable SQL Server instance's master connection.");
            sql.Pooling = false;
            sql.ConnectTimeout = 3;
            // SQL Server container has no health check in the existing CI.
            await using var master = new SqlConnection(sql.ConnectionString);
            for (var attempt = 0; ; attempt++)
            {
                try { await master.OpenAsync(ct); break; }
                catch (SqlException) when (attempt < 80) { await Task.Delay(1000, ct); }
            }
            var name = "idk_crash_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(master, $"CREATE DATABASE [{name}]", ct);
            sql.InitialCatalog = name;
            connectionString = sql.ConnectionString;
            try
            {
                await ExecuteAsync(master, $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON", ct);
            }
            catch
            {
                await DropSqlServerDatabaseAsync(source, connectionString);
                throw;
            }
        }

        try
        {
            await using var db = Connection(provider, connectionString);
            await db.OpenAsync(ct);
            var migration = await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "migrations", provider + ".sql"), ct);
            await ExecuteAsync(db, migration, ct);
            await ExecuteAsync(db, provider == "postgres"
                ? "CREATE TABLE IF NOT EXISTS ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity integer NOT NULL)"
                : "CREATE TABLE dbo.ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity int NOT NULL)", ct);
            return connectionString;
        }
        catch
        {
            if (provider == "sqlserver") await DropSqlServerDatabaseAsync(source, connectionString);
            throw;
        }
    }

    private static async Task ExecuteAsync(DbConnection db, string sql, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 45;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task DropSqlServerDatabaseAsync(string source, string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        // Guard the cleanup command even if the helper is called incorrectly.
        if (!name.StartsWith("idk_crash_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["idk_crash_".Length..], "N", out _))
            throw new InvalidOperationException("Refusing to drop a non-fixture database.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var master = new SqlConnection(new SqlConnectionStringBuilder(source)
            { Pooling = false }.ConnectionString);
        await master.OpenAsync(timeout.Token);
        await ExecuteAsync(master, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]", timeout.Token);
    }

    private static async Task<DatabaseState> ReadStateAsync(
        string provider, string cs, string key, string scope, CancellationToken ct)
    {
        await using var db = Connection(provider, cs);
        await db.OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandTimeout = 15;
        // A single statement gives a consistent snapshot of both tables under
        // PostgreSQL READ COMMITTED or SQL Server READ_COMMITTED_SNAPSHOT.
        var count = provider == "postgres" ? "count(*)" : "count_big(*)";
        var first = provider == "postgres" ? "" : "TOP (1) ";
        var limit = provider == "postgres" ? " LIMIT 1" : "";
        var prefix = provider == "postgres" ? "" : "dbo.";
        command.CommandText = $"""
            SELECT (SELECT {count} FROM {prefix}ik_crash_orders WHERE request_key=@key),
                   (SELECT {count} FROM {prefix}idempotencykit_records WHERE scope_key_hash=@scope),
                   (SELECT {first}id FROM {prefix}ik_crash_orders WHERE request_key=@key{limit}),
                   (SELECT {first}state FROM {prefix}idempotencykit_records WHERE scope_key_hash=@scope{limit}),
                   (SELECT {first}response_body FROM {prefix}idempotencykit_records WHERE scope_key_hash=@scope{limit})
            """;
        AddParameter(command, "@key", key);
        AddParameter(command, "@scope", scope);
        await using var row = await command.ExecuteReaderAsync(ct);
        Assert.True(await row.ReadAsync(ct));
        return new DatabaseState(row.GetInt64(0), row.GetInt64(1),
            row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetString(3),
            row.IsDBNull(4) ? null : (byte[])row.GetValue(4));
    }

    private sealed record DatabaseState(long Orders, long Records, string? OrderId,
        string? RecordState, byte[]? Body);

    private sealed class HostProcess(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        public Task<string> Errors { get; } = process.StandardError.ReadToEndAsync();
        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                    if (!Process.WaitForExit(5000))
                        throw new TimeoutException("Crash fixture did not exit.");
                }
            }
            finally { Process.Dispose(); }
        }
    }
}
