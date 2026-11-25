# Getting Started

**Start here:** [Provider quick-start matrix](QUICK_START.md) for all six framework/provider combinations, packages, migrations, actual test commands and retry handling. All packages are currently unpublished preview artifacts.

## Run the Orders demos

The two examples use **separate local SQLite files and a local-only demo token**. They are not production authentication examples. No database server or published package is needed. From the repository root, start either app:

~~~bash
dotnet run --project dotnet/samples/Orders.AspNetCore
# Or, in another shell:
cd typescript && npm ci && npm run start:orders
~~~

Keep the sample running and execute the HTTP check from the repository root:

~~~bash
node typescript/apps/orders-api/smoke.mjs http://127.0.0.1:5050
# Use http://127.0.0.1:3000 for NestJS.
~~~

Try a request yourself on the sample's port:

~~~bash
curl -i http://127.0.0.1:5050/orders \
  -H 'Authorization: Bearer orders-demo' \
  -H 'Idempotency-Key: order-demo-1' \
  -H 'Content-Type: application/json' \
  -d '{"productId":42,"quantity":2}'
~~~

Repeat it with the same key and payload: both the original and replay return `201` with identical bytes and `Location`. The same key with a changed quantity yields `422`, and authorized `GET /orders` observes only one new row. Change the port with `PORT` and the local file with `ORDERS_DB_PATH`; use different files for the two samples. Read the [Arabic walkthrough](WALKTHROUGH_AR.md) for a line-by-line path through the transaction.

## PostgreSQL Standard and Transactional mode

## Prerequisites

- .NET 10 SDK; a supported Node 24 runtime with npm.
- **PostgreSQL 15+** (CI tests against PostgreSQL 17).
- Apply [the shared migration](../spec/sql/postgres/001_idempotencykit_records.sql) before initializing either store.

An isolated test database can be started with Docker:

~~~bash
docker run --rm --name idempotencykit-test -e POSTGRES_PASSWORD=testpass -p 5432:5432 -d postgres:17
~~~

Apply the migration with PostgreSQL's `psql` (from the repository root):

~~~bash
psql "postgresql://postgres:testpass@127.0.0.1:5432/postgres" -f spec/sql/postgres/001_idempotencykit_records.sql
~~~

**Never use those demonstration credentials in a real deployment.** Use an isolated database, restricted accounts, TLS where appropriate, and an ordinary versioned migration workflow.

## .NET

From the repository root:

~~~bash
cd dotnet
dotnet restore IdempotencyKit.slnx
dotnet build IdempotencyKit.slnx -c Release
~~~

To run the **real PostgreSQL** test suite set `IDEMPOTENCYKIT_TEST_POSTGRES` to an isolated database connection string, then:

~~~bash
dotnet test IdempotencyKit.slnx -c Release
~~~

The `IdempotencyKit.Postgres.Tests` and `IdempotencyKit.AspNetCore.Tests` projects test storage, Minimal APIs, MVC annotations, auth and replay. `PostgresTransactionalTests` additionally prove atomic order insertion, concurrent retries and rollback on PostgreSQL backend termination. It **requires PostgreSQL**.

## TypeScript / NestJS

~~~bash
cd typescript
npm ci
npm run build
~~~

To run real PostgreSQL tests, set `IDEMPOTENCYKIT_TEST_POSTGRES_URL` to a database URL, then:

~~~bash
npm test
~~~

The `typescript/tests/postgres-v1.test.mjs`, `typescript/tests/http-standard-v1.test.mjs` and `typescript/tests/postgres-transactional-v1.test.mjs` suites skip database scenarios if the environment variable is absent. CI **always sets it**, so its success includes actual PostgreSQL tests.

## Transactional Mode (explicit callback API)

For database mutations that must commit atomically with the replay record, use `PostgresTransactionalExecutor` from the relevant PostgreSQL package. Both implementations open a transaction and provide **the exact database connection** that the callback must use. The callback returns a bounded JSON response which is saved **before** the single `COMMIT`.

See [Transactional Mode v1](../spec/TRANSACTIONAL_MODE_V1.md) for the Npgsql/`pg.PoolClient` callback API and [Transactional HTTP + EF Core](TRANSACTIONAL_HTTP_AND_EFCORE_V1.md) for `MapTransactionalPost`, EF Core shared-transaction contexts and NestJS `handleExpress`. **Do not stack Standard-mode decorators around the same transactional operation**.

## Store API outline

~~~text
TryClaim(scopeKeyHash, requestFingerprint, leaseDuration)
  => Acquired(ownerToken) | InProgress | Completed(response) | FingerprintMismatch | Uncertain

TryComplete(scopeKeyHash, requestFingerprint, ownerToken, response, retention)
  => true only when matching current unexpired lease

TryExtendLease(scopeKeyHash, ownerToken, leaseDuration)
  => true only for current unexpired owner

PurgeExpiredCompleted(batchSize)
  => number of removed completed records after their retention window
~~~

**Never automatically retry an `Uncertain` operation:** the prior worker could already have produced business effects. Applying the migration and calling store methods alone does not enable HTTP protection. Register the appropriate framework adapter, use trusted authentication, and read the Standard-mode crash-recovery limitations.

See [PostgreSQL Store v1](../spec/POSTGRES_STORE_V1.md), [Guarantees](../spec/GUARANTEES.md), and [Roadmap](ROADMAP.md).
