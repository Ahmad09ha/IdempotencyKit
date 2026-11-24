using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IdempotencyKit.Sqlite.CrashHost;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IdempotencyKit.Sqlite.Tests;

public sealed class SqliteHttpProcessCrashTests
{
    [Theory]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task KilledHttpProcessLeavesAtomicStateAndFreshProcessCanRetry(string stage)
    {
        // A unique on-disk database per scenario gives independent WAL files
        // and lets real processes coordinate through SQLite rather than shared memory.
        var temp = Directory.CreateTempSubdirectory("idk-http-crash-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var database = Path.Combine(temp.FullName, "orders.db");
        try
        {
            using var firstHost = StartHost(database, stage);
            var firstUrl = await WaitForSignalAsync(firstHost,
                SqliteProcessFixture.ListeningSignal, ct);
            using var client = LoopbackClient();
            using var initialRequest = CreateRequest(firstUrl);
            var pending = client.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            Assert.Equal(stage, await WaitForSignalAsync(firstHost,
                SqliteProcessFixture.BlockedSignal, ct));
            Assert.False(pending.IsCompleted, "The caller must not have received HTTP headers.");
            var wasCommitted = stage == "after-commit";

            // A separate connection confirms the actual durable handoff point:
            // either nothing is visible or BOTH the order and completed result are.
            var beforeKill = await ReadStateAsync(database, ct);
            AssertAtomicState(beforeKill, wasCommitted);
            Assert.False(pending.IsCompleted, "The caller must still not have response headers.");

            firstHost.Process.Kill(); // SIGKILL on CI Linux; never a graceful host shutdown.
            await firstHost.Process.WaitForExitAsync(ct);
            Assert.NotEqual(0, firstHost.Process.ExitCode);
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var unexpected = await pending;
            });

            var afterKill = await ReadStateAsync(database, ct);
            AssertAtomicState(afterKill, wasCommitted);
            Assert.Equal(beforeKill.OrderId, afterKill.OrderId);
            Assert.Equal(beforeKill.Body, afterKill.Body);

            using var retryHost = StartHost(database, "retry");
            var retryUrl = await WaitForSignalAsync(retryHost,
                SqliteProcessFixture.ListeningSignal, ct);
            using var retryRequest = CreateRequest(retryUrl);
            using var response = await client.SendAsync(retryRequest, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var json = JsonDocument.Parse(bytes);
            var orderId = json.RootElement.GetProperty("id").GetString();
            Assert.Equal(2, json.RootElement.GetProperty("quantity").GetInt32());
            Assert.Equal($"/orders/{orderId}", response.Headers.Location?.OriginalString);

            var afterRetry = await ReadStateAsync(database, ct);
            AssertAtomicState(afterRetry, committed: true);
            Assert.Equal(orderId, afterRetry.OrderId);
            Assert.Equal(bytes, afterRetry.Body);
            if (wasCommitted)
            {
                // The request returned the original recorded response, not a newly
                // executed callback that happens to return the same status code.
                Assert.Equal(beforeKill.OrderId, orderId);
                Assert.Equal(beforeKill.Body, bytes);
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static void AssertAtomicState(DatabaseState state, bool committed)
    {
        Assert.Equal(committed ? 1L : 0L, state.Orders);
        Assert.Equal(committed ? 1L : 0L, state.Records);
        Assert.Equal(committed ? "completed" : null, state.RecordState);
        Assert.Equal(committed, state.Body is { Length: > 0 });
    }

    private static HttpClient LoopbackClient() => new(new SocketsHttpHandler
    {
        UseProxy = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpRequestMessage CreateRequest(string baseUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + SqliteProcessFixture.Route)
        {
            Content = new StringContent("""{"quantity":2}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "sqlite-process-crash");
        request.Headers.Add(SqliteProcessFixture.AuthenticationHeader, SqliteProcessFixture.Subject);
        return request;
    }

    private static HostProcess StartHost(string database, string stage)
    {
        var runtimeDotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet))
            dotnet = File.Exists(runtimeDotnet) ? runtimeDotnet : "dotnet";

        var testAssembly = typeof(SqliteHttpProcessCrashTests).Assembly.Location;
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Use the already-built test dependency closure, which includes the real
        // ASP.NET runtime and portable SQLite native assets, to launch the fixture entry point.
        foreach (var arg in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(SqliteProcessFixture).Assembly.Location, database, stage
        })
            start.ArgumentList.Add(arg);
        return new HostProcess(Process.Start(start)
            ?? throw new InvalidOperationException("Unable to launch SQLite Kestrel crash fixture."));
    }

    private static async Task<string> WaitForSignalAsync(
        HostProcess host, string prefix, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        try
        {
            while (await host.Process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return line[prefix.Length..];
                diagnostics.Add(line);
            }
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException(
                $"SQLite crash host did not emit {prefix}: {string.Join(Environment.NewLine, diagnostics)}", error);
        }

        throw new InvalidOperationException(
            $"SQLite crash host exited before {prefix}: {string.Join(Environment.NewLine, diagnostics)}\n{await host.Errors.WaitAsync(cancellationToken)}");
    }

    private static async Task<DatabaseState> ReadStateAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM crash_orders),
                   (SELECT count(*) FROM idempotencykit_records),
                   (SELECT id FROM crash_orders LIMIT 1),
                   (SELECT state FROM idempotencykit_records LIMIT 1),
                   (SELECT response_body FROM idempotencykit_records LIMIT 1);
            """;
        await using var row = await command.ExecuteReaderAsync(ct);
        Assert.True(await row.ReadAsync(ct));
        return new DatabaseState(row.GetInt64(0), row.GetInt64(1),
            row.IsDBNull(2) ? null : row.GetString(2),
            row.IsDBNull(3) ? null : row.GetString(3),
            row.IsDBNull(4) ? null : (byte[])row.GetValue(4));
    }

    private sealed record DatabaseState(
        long Orders, long Records, string? OrderId, string? RecordState, byte[]? Body);

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
                    Process.WaitForExit(5000);
                }
            }
            finally { Process.Dispose(); }
        }
    }
}
