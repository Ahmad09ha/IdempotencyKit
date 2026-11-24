# SQLite EF Core 10 + authenticated transactional HTTP v1

**Status (2026-11-24): implemented and tested against actual local SQLite WAL database files.** This is source-level integration; NuGet/npm packages have **not** been published, and this is **not** a distributed storage or production audit certification.

**Prerequisite:** SQLite P3 database core, [SQLite Store v1](SQLITE_STORE_V1.md), and its initialization. Run `SqliteIdempotencyDatabase.InitializeAsync(file)` (.NET) or `initializeSqliteDatabase(file)` (Node) before handling requests. The **idempotency table and all business tables must be in the SAME physical SQLite file** and every business SQL statement must use the same supplied transaction.

New adapter paths:

- `dotnet/src/IdempotencyKit.EntityFrameworkCore.Sqlite` — `SqliteEfCoreTransactionalExecutor<TContext>`.
- `dotnet/src/IdempotencyKit.AspNetCore.Sqlite` — `AddSqliteTransactionalIdempotency` and `MapSqliteTransactionalPost`.
- `typescript/packages/nestjs-sqlite` — `SqliteTransactionalHttpModule.forRoot` and `SqliteTransactionalHttpService.handleExpress`, for **NestJS 12 + Express**.

All three adapters reuse the P3 SQLite transactional executor, v1 canonical JSON fingerprint and scoped SHA-256 identity. They **never** implement another independent transaction or replay store.

## Why SQLite needs different concurrency handling

SQLite WAL permits many readers but only **one writer for the entire database file**. The SQLite executor takes `BEGIN IMMEDIATE` before reading the scoped key and holds that write transaction through the business callback and persisted response, then commits once.

- Same idempotency key with a **committed matching processing record** → 409 `IDEMPOTENCY_IN_PROGRESS`, with `Retry-After: 1`.
- **Any `SQLITE_BUSY`/`SQLITE_LOCKED` error** (including a writer touching an **unrelated key or unrelated table**) → **storage error / HTTP 503**, **not** 409. A file lock alone cannot identify the lock owner.
- A committed matching completed row → exact saved HTTP status, body bytes and safe headers; **never** rerun its business callback.
- Same key with changed normalized request → 422 mismatch and no handler call.
- Expired or corrupt processing lease → conservative uncertain state / 409; no automatic business callback retry.
- Failed business code or invalid response capture **before COMMIT** → both SQLite business writes and the provisional idempotency claim roll back. A client may retry with the same key.
- An ambiguous COMMIT acknowledgement → **do not generate a new key or blindly repeat business code**. Retry the original key against the same authoritative SQLite file.

Keep business callbacks **short**. Avoid network calls, payments, email, queue publishing, unbounded delays, and external I/O while holding the file's only writer lock.

## .NET EF Core 10

The EF Core bridge creates a **fresh** DbContext using the executor-provided connection and enlists in the **exact existing SqliteTransaction**. It rejects any factory returning a context bound to a different connection (even another connection to the **same SQLite file**), or a context with an already-active transaction.

~~~csharp
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using IdempotencyKit.EntityFrameworkCore.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var file = Path.Combine(appDataDirectory, "orders.db");
await SqliteIdempotencyDatabase.InitializeAsync(file);

// OrdersDbContext and Order are your own app domain types.
// The factory MUST use the exact supplied connection object.
var efExecutor = new SqliteEfCoreTransactionalExecutor<OrdersDbContext>(
    new SqliteTransactionalExecutor(file, busyTimeoutSeconds: 2),
    connection => new OrdersDbContext(
        new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlite(connection).Options));

var result = await efExecutor.ExecuteAsync(
    scopeKeyHash, requestFingerprint,
    async (db, ct) =>
    {
        var order = new Order { ProductId = productId, Quantity = quantity };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct); // generated key INSIDE the transaction

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id = order.Id }),
            new Dictionary<string, string>
            {
                ["Location"] = "/orders/" + order.Id
            });
    },
    TimeSpan.FromHours(24), cancellationToken);

// result.Status: Executed, Replayed, InProgress,
// FingerprintMismatch, or Uncertain.
~~~

Remaining tracked EF entities are automatically saved by the bridge before transaction COMMIT. If business code throws **after** `SaveChangesAsync` or response validation fails, both domain data and the stored response reservation roll back.

**Do not use a separately injected scoped DbContext or unrelated SQLite connection for business writes**, and do not call `BeginTransaction`/`Commit` independently from the callback.

## ASP.NET Core authenticated JSON POST

The explicit mapping binds the original request JSON and v1 identity, authorizes the caller, and commits the handler's SQL with the stored JSON response before writing any HTTP result. It requires `AddIdempotencyKit` for the **trusted namespace/caller identity**, but does **not** use Standard-mode `[Idempotent]` or `WithIdempotency` annotations.

~~~csharp
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Sqlite;
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using System.Security.Claims;
using System.Text.Json;

var file = Path.Combine(appDataDirectory, "orders.db");
await SqliteIdempotencyDatabase.InitializeAsync(file);

// Configure a REAL authentication scheme (JWT/cookies) in the app.
builder.Services.AddAuthentication(/* your verified auth scheme */);
builder.Services.AddAuthorization();

builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-api";
    options.Retention = TimeSpan.FromHours(24);
    options.ResolveCaller = http => new IdempotencyCallerScope(
        http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
          ?? throw new InvalidOperationException("Authenticated principal required"),
        http.User.FindFirst("tenant_id")?.Value);
});
builder.Services.AddSqliteTransactionalIdempotency(
    file, busyTimeoutSeconds: 2);

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapSqliteTransactionalPost(
    "/orders", "orders.create.v1",
    async (http, bodyJson, connection, transaction, ct) =>
    {
        var order = JsonSerializer.Deserialize<CreateOrderDto>(
            bodyJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        // The business command MUST join the SAME SQLite transaction.
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO orders(product_id,quantity) VALUES (@p,@q) RETURNING id";
        insert.Parameters.AddWithValue("@p", order.ProductId);
        insert.Parameters.AddWithValue("@q", order.Quantity);
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id }),
            new Dictionary<string, string>
            {
                ["Location"] = "/orders/" + id
            });
    },
    (http, ct) => Task.FromResult(http.User.IsInRole("Orders.Writer")));

app.Run();

// Your app declares this DTO and its EF/domain entities.
public sealed record CreateOrderDto(int ProductId, int Quantity);
~~~

`MapSqliteTransactionalPost` attaches `RequireAuthorization()` and optionally invokes your current operation-level permission callback **before execution AND replay**. Properly configure authentication before routing any write traffic.

The example uses `appDataDirectory`, `builder` and domain types from the consuming application; it is an integration excerpt, not a complete standalone application.

## NestJS 12 / Express + Node.js 24

Node's `node:sqlite` driver is **synchronous**. The P3 SQLite adapter serializes its own operations against the same resolved local file **inside one process**, while another process still competes via SQLite's actual one-writer file lock. Keep transaction callbacks short even though their TypeScript signature returns a Promise.

First enable real raw body capture:

~~~typescript
const app = await NestFactory.create(AppModule, { rawBody: true });
~~~

Register an explicit SQLite transactional module **separate from Standard-mode NestJS idempotency interceptors**:

~~~typescript
import { Module } from "@nestjs/common";
import {
  initializeSqliteDatabase, SqliteTransactionalExecutor,
} from "@idempotencykit/sqlite";
import {
  SqliteTransactionalHttpModule,
} from "@idempotencykit/nestjs-sqlite";

const file = "/var/lib/orders/orders.db"; // MUST be a local on-disk path
initializeSqliteDatabase(file);

@Module({
  imports: [
    SqliteTransactionalHttpModule.forRoot({
      namespace: "orders-api",
      executor: new SqliteTransactionalExecutor(file, 250),
      retentionMs: 86_400_000,
      resolveCaller: req => {
        // MUST be a verified authenticated principal from a NestJS guard.
        const user = req.user as { id: string; tenantId?: string };
        return { principalId: user.id, tenantId: user.tenantId };
      },
    }),
  ],
})
export class AppModule {}
~~~

On a guarded controller that uses **Express** `@Req` and `@Res`:

~~~typescript
import { Controller, Post, Req, Res, UseGuards } from "@nestjs/common";
import type { Request, Response } from "express";
import {
  SqliteTransactionalHttpService,
} from "@idempotencykit/nestjs-sqlite";

@UseGuards(YourRealAuthenticationGuard) // configure in your application
@Controller("orders")
export class OrdersController {
  constructor(private readonly tx: SqliteTransactionalHttpService) {}

  @Post()
  async create(@Req() req: Request, @Res() res: Response) {
    return this.tx.handleExpress(req, res, "orders.create.v1",
      async (session, rawJson) => {
        const input = JSON.parse(rawJson!);
        const inserted = session.prepare(
          "INSERT INTO orders(product_id,quantity) VALUES (?,?)",
        ).run(input.productId, input.quantity);
        const id = Number(inserted.lastInsertRowid);
        return {
          statusCode: 201,
          contentType: "application/json",
          body: Buffer.from(JSON.stringify({ id })),
          headers: { Location: "/orders/" + id },
        };
      },
      req => Promise.resolve(/* fresh domain authorization */ true));
  }
}
~~~

The service validates the idempotency header, content type, raw body limits and trusted scope before domain execution. The handler receives a **restricted SQLite transaction statement session**; never open a second `DatabaseSync` or manually COMMIT. The service emits saved response bytes and safe headers **after** the executor finishes COMMIT, not from the handler.

- **NestJS Fastify is not supported** by the Express-only adapter.
- Do not stack the Standard-mode `@Idempotent()` decorator on these endpoints.
- Any `SQLITE_BUSY` or storage I/O problem becomes HTTP 503 with a same-key retry instruction.
- An authentication guard must run **before** the service; `resolveCaller` must not trust client-provided user/tenant headers.

## HTTP status and safety contract

| HTTP | Meaning | Client behavior |
| --- | --- | --- |
| 2xx | Newly executed or replayed durable JSON response | Reuse stored result |
| 400 | Missing/invalid key or invalid JSON | Fix request |
| 401 / 403 | Authentication missing / operation forbidden | Reauthenticate or obtain permission |
| 409 + Retry-After | Matching **committed processing row** | Retry same key after the suggested delay |
| 409 uncertain | Previous operation outcome unresolved | Do not execute a second action; reconcile |
| 422 | Same scoped key with changed request | Generate a **new key for a genuinely new operation only** |
| **503** | SQLite busy/locked, storage failure or uncertain COMMIT acknowledgement | Retry the **same key** with bounded backoff; do not treat as a proven duplicate |

Requests support up to **64 KiB original JSON**, stored replay bodies up to **256 KiB JSON**, and replay only allowlisted `Location`, `ETag`, and `Content-Language` headers.

The API's callback only makes SQL writes on the supplied transaction atomic. No external payment API, email, queue, unrelated SQLite file or remote database is covered.

## Verified acceptance tests and remaining limits

- .NET real-file tests cover EF Core context connection identity, generated keys, pending-tracked entities, committed result replay, error-after-SaveChanges rollback and concurrent EF Core requests.
- ASP.NET Core TestServer tests cover two host instances pointing to one SQLite file, exact status/body/Location replay, 401/403, 400/422, known processing/uncertain 409, 16-way retries, handler rollback and **unrelated writer → 503**.
- Node/NestJS Express tests cover guarded HTTP requests, tenant/principal identity, fresh permission checks, canonical JSON and raw bytes, mismatch, 409, handler rollback and 16-way contention.
- A separate actual NestJS Express worker server is **SIGKILLed before COMMIT** (both domain and idempotency rows roll back) and **after COMMIT but before reply** (same-key retry replays without a second INSERT).
- Both existing SQLite P3 core test suites continue to run; PostgreSQL and SQL Server CI regressions remain green.

**Not included:** full .NET API-process SIGKILL lab, multi-machine shared SQLite WAL, Fastify transport, NuGet/npm publication, complete backup/restore/disaster drills, filesystem permission/disk-full fault injection or performance/security audit. A fresh deployed server must use the **same authoritative local file** to replay; if it points to a different file, it is a different idempotency store.

For real-database test counts and CI links, see [Verification](VERIFICATION.md) and [Provider Rollout](DATABASE_PROVIDER_ROADMAP.md).
