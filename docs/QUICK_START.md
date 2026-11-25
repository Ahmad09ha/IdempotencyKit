# Provider quick start — unpublished preview

Choose a mode **before** wiring a route. Standard mode coordinates a claim and a separately executed handler; lease loss is uncertain and is not automatically retried. Transactional mode atomically stores the result with business SQL **on the supplied connection/transaction**. External payment/email/message side effects are outside that transaction. Do not stack Standard annotations on a Transactional endpoint.

## Choose the supported packages

All packages currently build as local `0.1.0-preview.1` artifacts; no NuGet/npm registry release exists. See [pack and install locally](RELEASING.md). The names below are package IDs, not a promise of registry availability.

| Provider | ASP.NET Core 10 Transactional HTTP | NestJS 12 / Express / Node 24 Transactional HTTP | Provider core / EF Core |
| --- | --- | --- | --- |
| PostgreSQL (CI: 17) | `IdempotencyKit.AspNetCore.Postgres` | `@idempotencykit/nestjs-postgres` | `IdempotencyKit.Postgres`, `@idempotencykit/postgres`, `IdempotencyKit.EntityFrameworkCore.Postgres` |
| SQL Server (CI: 2022) | `IdempotencyKit.AspNetCore.SqlServer` | `@idempotencykit/nestjs-sqlserver` | `IdempotencyKit.SqlServer`, `@idempotencykit/sqlserver`, `IdempotencyKit.EntityFrameworkCore.SqlServer` |
| SQLite (single host/local file) | `IdempotencyKit.AspNetCore.Sqlite` | `@idempotencykit/nestjs-sqlite` | `IdempotencyKit.Sqlite`, `@idempotencykit/sqlite`, `IdempotencyKit.EntityFrameworkCore.Sqlite` |

For Standard HTTP, select `IdempotencyKit.AspNetCore` or `@idempotencykit/nestjs` plus the corresponding provider core. Shared identity only: `IdempotencyKit.Core` or `@idempotencykit/core`. Do not install every provider.

## Initialize storage and start integration

| Provider | Storage setup | ASP.NET registration / route | NestJS module / callback |
| --- | --- | --- | --- |
| PostgreSQL | Apply [migration](../spec/sql/postgres/001_idempotencykit_records.sql); create app-owned NpgsqlDataSource or pg Pool. | `AddPostgresTransactionalIdempotency()` + `MapTransactionalPost(...)` | `PostgresTransactionalHttpModule.forRoot` + `PostgresTransactionalHttpService.handleExpress`; use supplied PoolClient |
| SQL Server | Apply [migration](../spec/sql/sqlserver/001_idempotencykit_records.sql); app-owned connection string or connected mssql pool. | `AddSqlServerTransactionalIdempotency(connectionString)` + `MapSqlServerTransactionalPost(...)` | `SqlServerTransactionalHttpModule.forRoot` + `SqlServerTransactionalHttpService.handleExpress`; use `new sql.Request(transaction)` |
| SQLite | `SqliteIdempotencyDatabase.InitializeAsync(file)` / `initializeSqliteDatabase(file)`; local disk, WAL, synchronous FULL. | `AddSqliteTransactionalIdempotency(file)` + `MapSqliteTransactionalPost(...)` | `SqliteTransactionalHttpModule.forRoot` + `SqliteTransactionalHttpService.handleExpress`; use supplied statement session |

Follow complete per-provider source-grounded examples: [PostgreSQL](TRANSACTIONAL_HTTP_AND_EFCORE_V1.md), [SQL Server](SQLSERVER_EFCORE_HTTP_V1.md), [SQLite](SQLITE_EFCORE_HTTP_V1.md), [Standard HTTP](HTTP_STANDARD_V1.md). They include both frameworks and the EF Core bridge. Apply migrations through your deployment mechanism; do not auto-migrate on install. All EF Core operations must use the exact connection object and transaction from the executor.

## Run without registry access

From a fresh clone, with .NET 10 SDK and Node 24:

~~~bash
dotnet run --project dotnet/samples/Orders.AspNetCore
# Separate terminal (NestJS):
cd typescript
npm ci
npm run start:orders
~~~

The sample endpoints are loopback only, use separate local files, and a deliberately public example token, not production auth. From the root:

~~~bash
node typescript/apps/orders-api/smoke.mjs http://127.0.0.1:5050
node typescript/apps/orders-api/smoke.mjs http://127.0.0.1:3000
~~~

For actual PostgreSQL and SQL Server provider tests use disposable instances and the environment variables/commands in [CI](../.github/workflows/). Never test with production connections. Start with [Getting Started](GETTING_STARTED.md) and [releasing](RELEASING.md).

## Checklist for your application

1. Register real authentication and current business authorization before replay. Derive tenant/principal from verified claims or guards; never from arbitrary client headers.
2. Stable service namespace and operation ID, consistent across replicas. Configure a retention window longer than expected client retries.
3. Require one unpredictable key per logical operation; keep the same key and payload on ambiguous network/503/409-in-progress retries. Different payload with same key means 422. For uncertain Standard state, reconcile with authoritative business data rather than retry automatically.
4. Use bounded original JSON (64 KiB request / 256 KiB response); return replayable success JSON (not 204/205/206), allowlisted ASCII headers, and encode URI components. Use `rawBody: true` for NestJS Express. Do not use raw/manual responses in the protected callback.
5. Use the supplied transaction exclusively for domain writes, including ORM work. Independently enforce business uniqueness (a different key can still describe the same business action).
6. Operate completed-only retention cleanup and [read-only health reports](OPERATIONS.md); protect stored bodies at rest and enforce rate/concurrency limits at the host. Observe DB availability and pool latency. Do not use SQLite on a shared network filesystem.

See [guarantees](../spec/GUARANTEES.md), [failure matrix](../spec/FAILURE_MATRIX.md) and [verification](VERIFICATION.md) before deployment. No production certification, independent audit or public package release is implied.
