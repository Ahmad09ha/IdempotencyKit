# PostgreSQL Transactional HTTP and EF Core integration v1

**Status:** initial provider-specific implementations, exercised in PostgreSQL 17 CI. The packages are source-only and not yet published to NuGet/npm or production-certified.

This milestone builds on [the explicit Transactional Mode executor](../spec/TRANSACTIONAL_MODE_V1.md). It enables real HTTP requests to claim and replay atomic PostgreSQL operations, and allows EF Core commands to share the **exact transaction** used to save their idempotency result.

## What is different from Standard mode?

| Mode | Route integration | Database guarantee |
| --- | --- | --- |
| Standard | ASP.NET Core `.WithIdempotency()` / `[Idempotent]` or NestJS `@Idempotent()` | Atomic claim and result storage only; **business writes can commit separately** |
| Transactional | ASP.NET Core `MapTransactionalPost` or NestJS `PostgresTransactionalHttpService.handleExpress` | Business SQL and completed idempotency result share **one PostgreSQL transaction** |
| EF Core Transactional | `PostgresEfCoreTransactionalExecutor<TDbContext>`, called explicitly | EF Core `SaveChangesAsync` and response record share that same PostgreSQL transaction |

**Do not combine Standard-mode annotations with a transactional executor for the same key.** They would create a separate claim first, leading to in-progress conflicts rather than one atomic domain operation.

## Separate provider-specific packages

Keeping transport- and persistence-specific code in distinct packages is deliberate, so future SQL Server and SQLite adapters can reuse fingerprinting and HTTP concepts without depending on Npgsql.

- `IdempotencyKit.Core`: scoped identities, canonical request fingerprints, storage contracts.
- `IdempotencyKit.AspNetCore`: database-neutral authenticated HTTP request preparation and Standard-mode integration.
- **`IdempotencyKit.AspNetCore.Postgres`:** transactional POST endpoint mapping and PostgreSQL executor DI registration.
- **`IdempotencyKit.EntityFrameworkCore.Postgres`:** fresh EF Core DbContext factory, verified shared Npgsql connection, `UseTransactionAsync` and `SaveChangesAsync`.
- `IdempotencyKit.Postgres`: Npgsql transaction executor + Standard-mode store.
- **`@idempotencykit/nestjs-postgres`:** explicit NestJS Express transactional HTTP service/module.
- `@idempotencykit/postgres`: node-postgres transaction executor.

## ASP.NET Core: transactional JSON POST endpoint

In the application's composition root, configure **real authentication**, the PostgreSQL data source and the IdempotencyKit service namespace:

~~~csharp
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.Postgres;
using IdempotencyKit.Core;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;

builder.Services.AddAuthentication(/* real JWT/cookie scheme */);
builder.Services.AddAuthorization();
builder.Services.AddSingleton(
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Main")!));

builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-api";
    options.Retention = TimeSpan.FromHours(24);
    options.ResolveCaller = http => new IdempotencyCallerScope(
        http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Authenticated principal missing"),
        http.User.FindFirst("tenant_id")?.Value);
});
builder.Services.AddPostgresTransactionalIdempotency(
    claimLockTimeout: TimeSpan.FromSeconds(2));

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotencyKit(); // Optional Standard-mode routes; does not claim transactional ones.

app.MapTransactionalPost("/orders", "orders.create.v1",
    async (http, rawJson, connection, transaction, ct) =>
    {
        var dto = JsonSerializer.Deserialize<CreateOrderDto>(rawJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // MUST use supplied connection AND transaction.
        await using var command = new NpgsqlCommand(
            "INSERT INTO orders(product_id,quantity) VALUES (@p,@q) RETURNING id",
            connection, transaction);
        command.Parameters.AddWithValue("p", dto.ProductId);
        command.Parameters.AddWithValue("q", dto.Quantity);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + id });
    },
    // Optional resource-level permission check runs BEFORE both execution and replay.
    (http, ct) => Task.FromResult(
        http.User.IsInRole("Orders.Writer")));

app.Run();
~~~

The `CreateOrderDto`, orders table, actual authentication/authorization provider and app configuration belong to the consumer. `MapTransactionalPost` automatically attaches `RequireAuthorization()`; it validates raw request bytes and scoped identity **before** opening the transaction. The optional authorization delegate must enforce any current, business-specific read/write permission needed before replay.

The supported route method in this first version is **POST**. Other database-aware verbs can be added through an explicit shared transport abstraction in a later release. The route callback returns replayable bytes; it **must not write directly to the HTTP response**, open an unrelated connection or commit the supplied transaction.

**HTTP decisions:** 2xx supported JSON for executed/replayed operations, 400 missing/malformed key/body, 403 failed operation authorization, 409 processing or uncertain, 422 mismatched fingerprint, and 503 storage/transaction unavailable. A 503 includes ambiguous COMMIT acknowledgements, which must be reconciled by retrying the **same key** against the primary.

## EF Core 10 + Npgsql: same transaction as response

This is a **separate EF Core adapter**, not an unverified promise that any injected DbContext automatically joins the PostgreSQL transaction.

~~~csharp
using IdempotencyKit.EntityFrameworkCore.Postgres;
using IdempotencyKit.Postgres;
using IdempotencyKit.Core;
using Microsoft.EntityFrameworkCore;

var executor = new PostgresEfCoreTransactionalExecutor<OrdersDbContext>(
    new PostgresTransactionalExecutor(dataSource),
    // The adapter verifies this DbContext uses the EXACT supplied connection.
    connection => new OrdersDbContext(
        new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connection)
            .Options));

var outcome = await executor.ExecuteAsync(
    scopeKeyHash, requestFingerprint,
    async (db, ct) =>
    {
        var order = new Order { ProductId = productId, Quantity = quantity };
        db.Orders.Add(order);
        // Save within the shared transaction to obtain generated order ID.
        await db.SaveChangesAsync(ct);

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id = order.Id }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + order.Id });
    },
    TimeSpan.FromHours(24), cancellationToken);
~~~

The DbContext factory MUST configure `UseNpgsql(connection)` with the passed-in **object**. The adapter verifies reference identity with `Database.GetDbConnection()`, enlists the context using `Database.UseTransactionAsync(transaction)`, and saves any remaining tracked changes before returning the response to the transaction executor. If the callback or final save fails, the PostgreSQL transaction rolls back **both** changes.

The context is created and disposed within the callback and **must not** be shared across requests or used concurrently. A normal independently scoped or pooled injected DbContext will NOT satisfy the shared-connection requirement without a deliberate factory/adapter.

## NestJS 12 / Express: transaction-aware service

Enable raw body capture **before** handling JSON writes:

~~~typescript
const app = await NestFactory.create(AppModule, { rawBody: true });
~~~

Register the separate module, not just `IdempotencyKitModule`:

~~~typescript
import { Module } from "@nestjs/common";
import { Pool } from "pg";
import { PostgresTransactionalExecutor } from "@idempotencykit/postgres";
import { PostgresTransactionalHttpModule } from "@idempotencykit/nestjs-postgres";

const pool = new Pool({ connectionString: process.env.DATABASE_URL });

@Module({
  imports: [PostgresTransactionalHttpModule.forRoot({
    namespace: "orders-api",
    executor: new PostgresTransactionalExecutor(pool),
    retentionMs: 86_400_000,
    resolveCaller: req => {
      // A successful REAL guard must have set req.user.
      const user = req.user as { id: string; tenantId?: string };
      return { principalId: user.id, tenantId: user.tenantId };
    },
  })],
})
export class AppModule {}
~~~

A controller owns its route; the service owns writing the response **after COMMIT**:

~~~typescript
@UseGuards(YourAuthGuard)
@Controller("orders")
export class OrdersController {
  constructor(private readonly tx: PostgresTransactionalHttpService) {}

  @Post()
  async create(@Req() req: Request, @Res() res: Response) {
    // Express only. Do NOT decorate with Standard-mode @Idempotent().
    return this.tx.handleExpress(req, res, "orders.create.v1",
      async (client, bodyJson) => {
        const dto = JSON.parse(bodyJson!);
        // MUST use this client, not pool.query() or a separately injected ORM.
        const { rows } = await client.query(
          "INSERT INTO orders(product_id, quantity) VALUES ($1,$2) RETURNING id",
          [dto.productId, dto.quantity]);
        const id = rows[0].id;
        return {
          statusCode: 201, contentType: "application/json",
          body: Buffer.from(JSON.stringify({ id })),
          headers: { Location: "/orders/" + id },
        };
      },
      // Optional fresh authorization predicate, including for cached replays.
      request => Promise.resolve(/* current permission from trusted context */ true));
  }
}
~~~

The `@Res()` parameter uses NestJS **Express raw response handling** intentionally: this allows replaying exactly the stored supported JSON bytes rather than applying a second, unrelated NestJS class serializer after the commit. The service, not the controller, ends the response. It will fail closed if `rawBody: true` is missing for a nonempty JSON request.

The `@UseGuards` decorator is compulsory for trusted req.user data; the adapter checks that a user exists but cannot establish whether it came from a secure authentication provider. Express adapter only; Fastify requires a separate implementation.

## Request and response boundaries

- Same v1 canonical scoped identity + SHA-256 fingerprint used by Standard mode.
- JSON POST with body ≤ **64 KiB**, UTF-8, no compressed content encoding.
- Successful 2xx JSON response bytes ≤ **256 KiB** and safe replay header allowlist (`Location`, `ETag`, `Content-Language`).
- New business operation → new client-supplied key; retry of same operation → **same key**.
- After COMMIT, response replay is authorized afresh. The scoped hash is not permission.
- A failed handler or invalid response rolls back both domain SQL and replay record.
- An ambiguous COMMIT acknowledgement cannot be translated into “the business write definitely failed.” Return 503; client retries with the **same key** and fingerprint.
- Different database connections, EF contexts, external API calls, emails, payments, outbox sends in separate transactions, and independent databases are **not** covered by this atomicity guarantee.
- Pending completed-record cleanup and business uniqueness constraints still matter.
- This is not universal exactly-once delivery, nor a published stable API.

## Verification

- [.NET EF Core tests](../dotnet/tests/IdempotencyKit.EFCore.Tests/EfCoreTransactionalTests.cs): EF-generated keys, shared transaction, rollback after `SaveChangesAsync`, wrong-connection rejection, and concurrent retries.
- [.NET HTTP tests](../dotnet/tests/IdempotencyKit.AspNetCore.Tests/HttpStandardTests.cs): transactional POST, multi-host replay, authorization, 422/400, failed writes and concurrency.
- [NestJS Express HTTP tests](../typescript/tests/http-transactional-v1.test.mjs): authenticated raw HTTP routes, replay, conflict/mismatch, auth scope, rollback and concurrency.

All integration scenarios run against **real PostgreSQL 17** in GitHub Actions. The test identity headers are test-only simulated authentication, **not** a production authentication pattern. For verified CI results and exclusions, see [Verification Evidence](VERIFICATION.md).
