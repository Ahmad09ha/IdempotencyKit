using System.Globalization;
using System.Text.Json;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;

// Test subprocess only: the Node test runner owns the local database and table.
if (args.Length != 4 || !int.TryParse(args[2], NumberStyles.None,
        CultureInfo.InvariantCulture, out var quantity) ||
    args[3] is not ("execute" or "hold"))
{
    throw new ArgumentException("Expected: database path, client key, quantity, execute|hold.");
}

const string operationId = "orders.create.v1";
var clientKey = args[1];
var scopeKey = RequestIdentityHasher.ComputeScopeKey(
    new IdempotencyRequestScope("sqlite-interop-test", operationId, "alice", clientKey, "acme"));
var fingerprint = RequestFingerprinter.Compute(new RequestFingerprintInput(
    "POST", operationId, "/orders", BodyJson: JsonSerializer.Serialize(new { quantity })));
var executor = new SqliteTransactionalExecutor(args[0], busyTimeoutSeconds: 2);
var callbackCalls = 0;

var outcome = await executor.ExecuteAsync(scopeKey, fingerprint, async (connection, transaction, ct) =>
{
    callbackCalls++;
    await using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO interop_orders(scope_key_hash, quantity)
        VALUES ($scope, $quantity) RETURNING id;
        """;
    command.Parameters.AddWithValue("$scope", scopeKey);
    command.Parameters.AddWithValue("$quantity", quantity);
    var id = (long)(await command.ExecuteScalarAsync(ct))!;

    if (args[3] == "hold")
    {
        // The second runtime attempts the same key while this transaction owns
        // the file's writer lock. COMMIT only after the parent has observed it.
        Console.WriteLine("writer-held");
        if (await Console.In.ReadLineAsync(ct) != "commit")
            throw new InvalidOperationException("Parent did not authorize the test commit.");
    }

    return new StoredHttpResponse(201, "application/json; charset=utf-8",
        JsonSerializer.SerializeToUtf8Bytes(new { id, quantity, runtime = "dotnet", note = "طلب" }),
        new Dictionary<string, string> { ["Location"] = $"/orders/{id}" });
}, TimeSpan.FromHours(1));

Console.WriteLine(JsonSerializer.Serialize(new
{
    scopeKey,
    fingerprint,
    status = outcome.Status.ToString() switch
    {
        "Executed" => "executed",
        "Replayed" => "replayed",
        "FingerprintMismatch" => "fingerprint_mismatch",
        var other => other
    },
    callbackCalls,
    response = outcome.Response is { } response
        ? new
        {
            statusCode = response.StatusCode,
            contentType = response.ContentType,
            body = Convert.ToBase64String(response.Body),
            headers = response.Headers
        }
        : null
}));
