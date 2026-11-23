# Transactional Mode v1 — single PostgreSQL transaction

**Status:** native Npgsql and node-postgres transaction executors are implemented in source, with real PostgreSQL integration tests. This is **not yet** a framework-transparent decorator, public package release, EF Core ORM adapter, or cross-database exactly-once system.

## The problem Transactional Mode solves

Standard-mode HTTP integration uses two distinct commits:

~~~text
idempotency claim -> business database commit -> idempotency completion
~~~

If the worker crashes after the business commit, the stored idempotency result may still be unknown. It is dangerous to blindly repeat the operation.

Transactional Mode requires the business SQL **and** completed idempotency response to participate in **one PostgreSQL ACID transaction**:

~~~text
BEGIN
  insert uncommitted idempotency reservation (unique scope key)
  execute business SQL on the SAME connection/transaction
  serialize and validate HTTP response
  mark idempotency record completed, still in same transaction
COMMIT (both effects or neither)
send response
~~~

- **Failure or rollback before COMMIT:** neither the business mutation nor idempotency completion survives.
- **Commit succeeds but HTTP response is lost:** the completed row and business mutation both exist; same-key retry replays.
- **Concurrent same scoped key:** unique-key insert serializes contenders. A short claim lock wait can return `InProgress` without executing the callback.
- **Same key, different fingerprint:** no callback; returns `FingerprintMismatch`.
- **Existing Standard-mode processing/uncertain record:** no callback. It returns `InProgress` or `Uncertain` (including expired processing lease) instead of dangerously taking over.
- **Already completed (Standard or Transactional) record:** replay from the shared table.

This provides an atomicity boundary **only for SQL actually executed within the provided transaction**. Calls to external services, queue brokers, email or payment gateways cannot be rolled back by PostgreSQL.

## Same PostgreSQL schema

Transactional Mode reuses [`idempotencykit_records`](sql/postgres/001_idempotencykit_records.sql). No second migration is needed.

The initial processing reservation is **uncommitted**. It is not visible to other transactions, and cannot be stolen by an expired lease while that transaction is open. The reservation is updated to `completed` before `COMMIT`. The transaction's unique-index conflict lock—not a separate long-running lease—is the concurrency mechanism.

The default transaction claim lock wait is 2 seconds, configurable from 1 ms to 30 seconds. Under contention the caller can receive `InProgress` and retry with the **same** key. The configured claim wait is reset before calling business SQL; it is not intended to silently time-limit unrelated domain queries.

The executor starts its own `READ COMMITTED` transaction and owns its commit/rollback. Transaction participation is **not implicit**.

## .NET 10 / Npgsql implementation

Use [`PostgresTransactionalExecutor`](../dotnet/src/IdempotencyKit.Postgres/PostgresTransactionalExecutor.cs) in the `IdempotencyKit.Postgres` project.

~~~csharp
using System.Text.Json;
using IdempotencyKit.Core;
using IdempotencyKit.Postgres;
using Npgsql;

// dataSource is your app's configured NpgsqlDataSource.
// scopeKeyHash/fingerprint are computed from trusted authenticated scope and
// original request data using the shared v1 core hashing functions.
var executor = new PostgresTransactionalExecutor(dataSource);

var outcome = await executor.ExecuteAsync(
    scopeKeyHash,
    fingerprint,
    async (connection, transaction, ct) =>
    {
        // IMPORTANT: every domain command must use BOTH provided objects.
        await using var command = new NpgsqlCommand(
            "INSERT INTO orders (product_id, quantity) " +
            "VALUES (@product, @quantity) RETURNING id",
            connection, transaction);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("quantity", quantity);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;

        // Serialize the replayable result BEFORE the transaction commits.
        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id, quantity }),
            new Dictionary<string, string> { ["Location"] = $"/orders/{id}" });
    },
    TimeSpan.FromHours(24),
    cancellationToken);

// Executed/Replayed: outcome.Response contains persisted 2xx JSON + headers.
// InProgress: 409/retry; FingerprintMismatch: 422; Uncertain: 409/reconcile.
// Your endpoint must map statuses and replay bytes to a real HTTP response.
~~~

The `orders` table, variables, authentication and error response mapping belong to the consuming application. Never call `transaction.CommitAsync()` or `RollbackAsync()` from inside the callback; the executor owns the transaction. Do not perform any business writes through a different connection or an independent `DbContext`.

**EF Core note:** this first implementation provides a typed `NpgsqlConnection` and `NpgsqlTransaction` callback, **not** a prepackaged EF Core integration. A future adapter can join a `DbContext` to that same underlying transaction using `UseTransactionAsync` when the provider/connection ownership is configured correctly. Do not assume ordinary injected EF DbContexts automatically participate.

## NestJS / TypeScript implementation

The same operation is available from [`@idempotencykit/postgres`](../typescript/packages/postgres/src/postgres-transactional-executor.ts), using node-postgres:

~~~typescript
import { Pool } from "pg";
import { PostgresTransactionalExecutor } from "@idempotencykit/postgres";

const pool = new Pool({ connectionString: process.env.DATABASE_URL });
const executor = new PostgresTransactionalExecutor(pool);

const outcome = await executor.execute(
  scopeKeyHash,
  fingerprint,
  async client => {
    // MUST use client.query (same connection and transaction).
    // Never use pool.query or a different client inside this callback.
    const { rows } = await client.query(
      "INSERT INTO orders(product_id, quantity) " +
      "VALUES ($1, $2) RETURNING id",
      [productId, quantity],
    );
    const id = rows[0].id;
    return {
      statusCode: 201,
      contentType: "application/json",
      body: Buffer.from(JSON.stringify({ id, quantity }), "utf8"),
      headers: { Location: "/orders/" + id },
    };
  },
  86_400_000, // 24-hour retention in milliseconds
);

// outcome.status === "executed" | "replayed" | "in_progress"
//                   | "fingerprint_mismatch" | "uncertain"
// Your service/controller maps this to a real HTTP response.
~~~

The pool is application-owned and must be closed at shutdown. The callback receives a `pg.PoolClient` bound to the already-open transaction. **Do not issue BEGIN/COMMIT/ROLLBACK inside the callback** or use NestJS ORM repositories that silently open another connection.

## Interaction with HTTP Standard mode

**Do not apply `[Idempotent]` or `@Idempotent` to a route that calls the transactional executor for the same scope/key.** Those existing annotations currently claim the key through **Standard mode** before invoking a handler. A nested transactional attempt would encounter an in-progress Standard claim.

Instead, handle request key/fingerprint validation and authorization in your own controller/service, call the transactional executor **as the single owner of the operation**, and map its outcome explicitly. A future dedicated Transactional-mode HTTP integration will streamline this.

The core fingerprints and scoped identities are shared: both modes intentionally use the same `idempotencykit_records` table and canonical hash versions.

## HTTP mapping suggestion

| Result | Endpoint behavior |
| --- | --- |
| `Executed` | Return newly committed supported status/body/headers |
| `Replayed` | Replay persisted supported status/body/headers |
| `InProgress` | Return 409 and optional Retry-After (same key on retry) |
| `FingerprintMismatch` | Return 422; do not execute |
| `Uncertain` | Return 409; require reconciliation |

Authentication and **current authorization** must be checked before replaying stored data; the transaction executor cannot infer end-user permissions. For malformed or missing key, reject before calling the executor. Unknown DB errors must fail closed.

Supported responses are JSON 2xx with raw UTF-8 JSON bytes up to **256 KiB**, replayable headers limited to `Location`, `ETag` and `Content-Language`; cookie, authorization, and arbitrary headers are not saved. The response is validated **before COMMIT**; a serialization, header, or response validation failure rolls back the business row too.

## Commit acknowledgement ambiguity

A database COMMIT may have completed on the server when the connection dies before the client receives the acknowledgement. Neither executor **automatically retries its callback** on an error. The caller must resolve the outcome by querying/retrying **the same scoped key** against the authoritative PostgreSQL primary under the same fingerprint. Never blindly generate a new idempotency key for the same logical operation.

Be careful with asynchronous replication/failover, which can alter durability/visibility. PostgreSQL durability settings (`synchronous_commit`, WAL, replication, backup/failover policy) are part of the actual guarantee.

## Integration test coverage and remaining limitations

The tests use PostgreSQL 17 in GitHub Actions:

- [.NET transactional tests](../dotnet/tests/IdempotencyKit.Postgres.Tests/PostgresTransactionalTests.cs).
- [TypeScript transactional tests](../typescript/tests/postgres-transactional-v1.test.mjs).

Scenarios include successful commit+replay, changed fingerprint, callback exception rollback, unsupported response rollback, 24 concurrent contenders, Standard-mode interoperability, and deliberate PostgreSQL backend termination before COMMIT. The Node suite also checks uncommitted rows remain invisible and a bounded lock wait returns `InProgress`.

**Deliberately terminating a PostgreSQL backend is a database-connection failure simulation, NOT a full operating-system SIGKILL failure lab of an API process.** The latter, plus kill-after-commit-before-response tests, EF Core integration, and ORM/provider-specific transaction adapters remain future work. This is an initial narrowly scoped PostgreSQL guarantee, **not** release certification.
