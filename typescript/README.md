# NestJS / TypeScript workspace

This npm workspace includes an **implemented Standard-mode PostgreSQL store** plus a TypeScript core, and a working NestJS Standard-mode HTTP interceptor/decorator for supported Express JSON routes.

- `@idempotencykit/core`: scoped hashes, deterministic JSON fingerprints, `IdempotencyStore`, and generic `TransactionalIdempotencyExecutor<TSession>` / canonical outcome types.
- `@idempotencykit/postgres`: PostgreSQL Standard-mode store and `PostgresTransactionalExecutor` implementing the shared `TransactionalIdempotencyExecutor<PoolClient>`.
- `@idempotencykit/sqlserver`: SQL Server 2022 Standard-mode store and `SqlServerTransactionalExecutor` using transaction-owned `sp_getapplock` and the same `mssql.Transaction` for domain writes.
- `@idempotencykit/nestjs`: Standard-mode module, decorator and interceptor.
- `@idempotencykit/nestjs-postgres`: `PostgresTransactionalHttpModule.forRoot` and `PostgresTransactionalHttpService.handleExpress` for guarded transaction-aware Express routes; requires `rawBody: true`.
- `@idempotencykit/orders-api`: `GET /health` only; no protected write routes.

**Provider expansion:** SQL Server Standard + Transactional core is implemented; SQLite and SQL Server-specific NestJS HTTP are planned. The shared transactional outcome fixture is executed in both languages; see [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md).

## Build

~~~bash
cd typescript
npm install
npm run build
~~~

## PostgreSQL tests

Apply the migration `../spec/sql/postgres/001_idempotencykit_records.sql`. Set `IDEMPOTENCYKIT_TEST_POSTGRES_URL` to an **isolated** test database, e.g. `postgresql://postgres:testpass@127.0.0.1:5432/postgres`, then:

~~~bash
npm test
~~~

When the environment variable is absent, the PostgreSQL-specific Node tests **skip**; CI always supplies a real PostgreSQL 17 server to exercise them. The test suite covers Standard-mode claims, replay and leases, plus Transactional-mode business-table rollbacks, 24-way concurrency, completion replay and PostgreSQL backend termination.

## Programmatic storage usage (illustration)

~~~typescript
import pg from "pg";
import { PostgresIdempotencyStore } from "@idempotencykit/postgres";
const pool = new pg.Pool({ connectionString: process.env.DATABASE_URL });
const store = new PostgresIdempotencyStore(pool);
const claim = await store.tryClaim(scopeKeyHash, fingerprint, 30_000);
// Check claim.status before deciding whether to execute the business handler.
~~~

Caller must authenticate/authorize, construct the scoped identity, handle the claim outcome and use the correct policy for Standard-mode uncertain results. **Routes with `@Idempotent(...)` can now be protected when the module, PostgreSQL store, trusted auth guard and raw-body capture are configured correctly. This is Standard mode only.**

Use `PostgresTransactionalExecutor.execute(...)` for business callbacks or `PostgresTransactionalHttpService.handleExpress(...)` for guarded NestJS Express routes. Do **not** wrap transactional operations with Standard-mode `@Idempotent`. See [Transactional HTTP + EF Core](../docs/TRANSACTIONAL_HTTP_AND_EFCORE_V1.md).

All packages remain `private`/`0.0.0`; do not publish before the API, operational contract, and tests are finalized.

See [HTTP integration](../docs/HTTP_STANDARD_V1.md), [PostgreSQL Store](../spec/POSTGRES_STORE_V1.md), and [Getting Started](../docs/GETTING_STARTED.md).
