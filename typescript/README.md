# NestJS / TypeScript workspace

This npm workspace includes an **implemented Standard-mode PostgreSQL store** plus a TypeScript core, but the NestJS HTTP integration is **still a scaffold**.

- `@idempotencykit/core`: scoped request hashes, deterministic JSON fingerprints and `IdempotencyStore` contracts.
- `@idempotencykit/postgres`: node-postgres implementation using PostgreSQL's unique constraints and atomic updates.
- `@idempotencykit/nestjs`: empty module, **no interceptor/decorator yet**.
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

When the environment variable is absent, the PostgreSQL-specific Node tests **skip**; CI always supplies a real PostgreSQL 17 server to exercise them. The test suite covers concurrent atomic claims, mismatches, replay, owner fencing, expiry, response limits and cleanup.

## Programmatic storage usage (illustration)

~~~typescript
import pg from "pg";
import { PostgresIdempotencyStore } from "@idempotencykit/postgres";
const pool = new pg.Pool({ connectionString: process.env.DATABASE_URL });
const store = new PostgresIdempotencyStore(pool);
const claim = await store.tryClaim(scopeKeyHash, fingerprint, 30_000);
// Check claim.status before deciding whether to execute the business handler.
~~~

Caller must authenticate/authorize, construct the scoped identity, handle the claim outcome and use the correct policy for Standard-mode uncertain results. **This package does not yet transparently protect NestJS routes.**

All packages remain `private`/`0.0.0`; do not publish before the API, operational contract, and tests are finalized.

See `../spec/POSTGRES_STORE_V1.md` and `../docs/GETTING_STARTED.md`.
