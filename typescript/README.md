# NestJS / TypeScript workspace

This npm workspace includes an **implemented Standard-mode PostgreSQL store** plus a TypeScript core, and a working NestJS Standard-mode HTTP interceptor/decorator for supported Express JSON routes.

- `@idempotencykit/core`: scoped request hashes, deterministic JSON fingerprints and `IdempotencyStore` contracts.
- `@idempotencykit/postgres`: Standard-mode store plus `PostgresTransactionalExecutor`, which atomically commits SQL executed on a supplied `pg.PoolClient` with the replay record.
- `@idempotencykit/nestjs`: Standard-mode module, decorator and interceptor.
- `@idempotencykit/nestjs-postgres`: `PostgresTransactionalHttpModule.forRoot` and `PostgresTransactionalHttpService.handleExpress` for guarded transaction-aware Express routes; requires `rawBody: true`.
- `@idempotencykit/orders-api`: `GET /health` only; no protected write routes.

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
