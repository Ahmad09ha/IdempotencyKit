# SQL Server EF Core + transactional HTTP integrations v1

**Status:** explicit SQL Server 2022 provider-specific source integrations for .NET 10, EF Core 10, ASP.NET Core, and NestJS 12/Express. These are source packages, **not published**, and are not a production/Azure SQL failover certification. Test evidence: [Verification](VERIFICATION.md).

**Prerequisite:** deploy [`spec/sql/sqlserver/001_idempotencykit_records.sql`](../spec/sql/sqlserver/001_idempotencykit_records.sql) in the **same SQL Server database** as your domain tables. The transaction-owned key lock resource is shared by Standard and Transactional modes.

## Why explicit SQL Server integrations?

- `IdempotencyKit.Core` / `@idempotencykit/core` computes the **same v1 scoped SHA-256 and request fingerprint** for every database.
- `IdempotencyKit.SqlServer` / `@idempotencykit/sqlserver` owns atomic SQL Server claims, BEGIN/COMMIT/ROLLBACK, safe JSON response persistence and replay.
- `IdempotencyKit.EntityFrameworkCore.SqlServer` constructs a **fresh EF Core DbContext on the exact SqlConnection** provided by the executor and enlists it in the **same SqlTransaction**.
- `IdempotencyKit.AspNetCore.SqlServer` maps an explicit authenticated transaction-aware JSON POST endpoint.
- `@idempotencykit/nestjs-sqlserver` exposes an explicit guarded NestJS **Express** service that writes the stored response bytes after COMMIT.

**Never stack** Standard-mode `[Idempotent]`, `.WithIdempotency(...)` or NestJS `@Idempotent()` around a Transactional-mode operation. These Standard-mode annotations would create an independent claim and violate the one-owner execution model.

## .NET EF Core 10 + Microsoft.Data.SqlClient

The EF Core adapter does **not** magically enlist a regular injected `DbContext` from DI. It constructs one fresh context with the very same SQL Server connection object and checks it before executing business SQL:

~~~csharp
using IdempotencyKit.EntityFrameworkCore.SqlServer;
using IdempotencyKit.SqlServer;
using IdempotencyKit.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

// DbContext type, domain entity, auth scope and app's connectionString
// are owned by the consuming application.
var efExecutor = new SqlServerEfCoreTransactionalExecutor<OrdersDbContext>(
    new SqlServerTransactionalExecutor(connectionString),
    connection => new OrdersDbContext(
        new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlServer(connection)
            .Options));

var result = await efExecutor.ExecuteAsync(
    scopeKeyHash, requestFingerprint,
    async (db, ct) =>
    {
        var order = new Order { ProductId = productId, Quantity = quantity };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct); // key generation INSIDE transaction

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id = order.Id }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + order.Id });
    },
    TimeSpan.FromHours(24), cancellationToken);

// result.Status: Executed / Replayed / InProgress / FingerprintMismatch / Uncertain
~~~

The bridge rejects contexts that use a different connection, already have another active transaction, or cannot join the supplied `SqlTransaction`. The bridge also saves pending tracked changes before the executor commits. If the handler throws after `SaveChangesAsync`, both the business row and response record roll back. If COMMIT acknowledgement is lost, **retry with the same key** against the authoritative SQL Server primary; never blindly execute the callback again.

The DbContext factory must provide a **relational EF Core SQL Server provider**, e.g. `UseSqlServer(connection)`; independently injected scoped DbContexts do not join automatically. Do not use independently connected repositories, payments, email or queues inside the expectation of single-database atomicity.

## ASP.NET Core authenticated JSON POST

~~~csharp
using IdempotencyKit.AspNetCore;
using IdempotencyKit.AspNetCore.SqlServer;
using IdempotencyKit.Core;
using Microsoft.Data.SqlClient;
using System.Security.Claims;
using System.Text.Json;

builder.Services.AddAuthentication(/* configure a REAL JWT/cookie scheme */);
builder.Services.AddAuthorization();

builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-api";
    options.Retention = TimeSpan.FromHours(24);
    options.ResolveCaller = http => new IdempotencyCallerScope(
        http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Missing authenticated user"),
        http.User.FindFirst("tenant_id")?.Value);
});

builder.Services.AddSqlServerTransactionalIdempotency(
    builder.Configuration.GetConnectionString("Main")!);

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapSqlServerTransactionalPost(
    "/orders", "orders.create.v1",
    async (http, bodyJson, connection, transaction, ct) =>
    {
        var order = JsonSerializer.Deserialize<CreateOrderDto>(bodyJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Every business SQL command MUST use these exact objects.
        await using var insert = new SqlCommand(
            "INSERT INTO dbo.orders(product_id, quantity) " +
            "OUTPUT INSERTED.id VALUES (@p,@q)",
            connection, transaction);
        insert.Parameters.AddWithValue("@p", order.ProductId);
        insert.Parameters.AddWithValue("@q", order.Quantity);
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + id });
    },
    (http, ct) => Task.FromResult(http.User.IsInRole("Orders.Writer")));

app.Run();
~~~

The `CreateOrderDto` type, real authentication/authorization, domain table, and connection configuration are defined by the app. `MapSqlServerTransactionalPost` attaches `RequireAuthorization()`. The optional operation permission check executes **before both new execution and replay**. Middleware `UseIdempotencyKit()` can be used for other Standard-mode routes, but must not be annotated onto the same transactional endpoint.

HTTP responses: 2xx executed/replayed JSON, 400 missing/invalid key or JSON, 401/403 authentication/authorization, 409 processing or uncertain, 422 mismatched fingerprint, and 503 storage/transaction unavailable. A 503 can indicate **ambiguous COMMIT acknowledgement**, so retry with the **same key** rather than generating a new one.

## NestJS 12 + Express transactional SQL Server route

For **NestJS Express only**, enable raw JSON capture:

~~~typescript
const app = await NestFactory.create(AppModule, { rawBody: true });
~~~

Register the SQL Server adapter **separately** from Standard-mode `IdempotencyKitModule`:

~~~typescript
import { Module } from "@nestjs/common";
import sql from "mssql";
import { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";
import { SqlServerTransactionalHttpModule } from "@idempotencykit/nestjs-sqlserver";

const pool = await new sql.ConnectionPool({
  server: process.env.SQLSERVER_HOST!,
  database: process.env.SQLSERVER_DATABASE!,
  user: process.env.SQLSERVER_USER!,
  password: process.env.SQLSERVER_PASSWORD!,
  options: { encrypt: true }, // verify TLS certificates in production
}).connect();

@Module({
  imports: [
    SqlServerTransactionalHttpModule.forRoot({
      namespace: "orders-api",
      executor: new SqlServerTransactionalExecutor(pool),
      retentionMs: 86_400_000,
      resolveCaller: req => {
        // A verified auth guard MUST populate this from a trusted token/session.
        const user = req.user as { id: string; tenantId?: string };
        return { principalId: user.id, tenantId: user.tenantId };
      },
    }),
  ],
})
export class AppModule {}
~~~

Guard the controller with your real auth guard; let the service finish the Express response:

~~~typescript
import { Controller, Post, Req, Res, UseGuards } from "@nestjs/common";
import sql from "mssql";
import { SqlServerTransactionalHttpService } from "@idempotencykit/nestjs-sqlserver";
import type { Request, Response } from "express";

@UseGuards(YourRealAuthGuard)
@Controller("orders")
export class OrdersController {
  constructor(private readonly tx: SqlServerTransactionalHttpService) {}

  @Post()
  async create(@Req() req: Request, @Res() res: Response) {
    return this.tx.handleExpress(req, res, "orders.create.v1",
      async (transaction, json) => {
        const body = JSON.parse(json!);
        const result = await new sql.Request(transaction) // SAME transaction
          .input("productId", sql.Int, body.productId)
          .input("qty", sql.Int, body.quantity)
          .query(
            "INSERT INTO dbo.orders(product_id,quantity) " +
            "OUTPUT INSERTED.id VALUES (@productId,@qty)");

        const id = Number(result.recordset[0].id);
        return {
          statusCode: 201,
          contentType: "application/json",
          body: Buffer.from(JSON.stringify({ id })),
          headers: { Location: "/orders/" + id },
        };
      },
      req => Promise.resolve(/* fresh resource authorization */ true));
  }
}
~~~

`req.user` must come from an **authenticated guard**, not raw client-supplied identity headers. The service verifies body limits and generates the v1 fingerprint from original bytes before the handler. Successful operations and replay emit **the stored JSON bytes and safe header subset**. No NestJS serialization or HTTP response writes inside the transaction callback. Avoid `pool.request()` in business callbacks; it uses an **independent** connection, not the supplied transaction.

The service rejects missing `rawBody: true` for nonempty JSON requests. **Fastify is not supported** in this API.

## Verified constraints and pending hardening

- JSON request body ≤ **64 KiB**; retained JSON response ≤ **256 KiB**; content type `application/json` (responses may also use `application/problem+json`).
- Replayable response headers: only `Location`, `ETag`, and `Content-Language`, validated at persistence and on read.
- Key identity includes namespace, operation ID, trusted principal, optional trusted tenant, and client-supplied key. Fingerprints include normalized URL, method and canonical JSON.
- Same scoped key/changed body => 422; same request while owned by a live transaction => 409, with `Retry-After`; replay requires reauthorization.
- This does not deliver exactly-once payments, messages, email, external APIs, independent databases or arbitrary side effects.
- Local SQL Server 2022 CI validates transactional behavior; **Azure SQL failover, replication, API-process restart in .NET, cross-language mixed-run concurrency, security/performance audit, package publication and reconciliation are NOT certified**.
- SQL Server route and EF Core tests are kept provider-specific, so PostgreSQL integration and its previous guarantees remain unchanged.

See [SQL Server Core v1](../spec/SQLSERVER_STORE_V1.md), [Provider Architecture](../spec/PROVIDER_ARCHITECTURE_V1.md), and [Database Provider Roadmap](DATABASE_PROVIDER_ROADMAP.md).
