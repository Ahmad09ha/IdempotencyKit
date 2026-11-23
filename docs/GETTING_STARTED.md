# Getting Started: Core libraries and PostgreSQL Standard-mode storage

> **Important:** There is no active HTTP protection in either Orders sample yet. This page shows how to build and test the core and storage layers. A successful health check does NOT demonstrate idempotency.

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

The new `IdempotencyKit.Postgres.Tests` project tests concurrent claims, replay, owner fencing, lease expiry, unsafe response headers and retention cleanup. It **requires PostgreSQL**. `Orders.AspNetCore` still serves only `GET /health`.

## TypeScript / NestJS

~~~bash
cd typescript
npm install
npm run build
~~~

To run real PostgreSQL tests, set `IDEMPOTENCYKIT_TEST_POSTGRES_URL` to a database URL, then:

~~~bash
npm test
~~~

The `typescript/tests/postgres-v1.test.mjs` integration suite skips database scenarios if the environment variable is absent. CI **always sets it**, so its success includes actual PostgreSQL tests. The NestJS sample still serves only `GET /health`.

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

**Never automatically retry an `Uncertain` operation:** the prior worker could already have produced business effects. Applying the migration and calling these storage methods is not the same as enabling an idempotent controller or NestJS interceptor.

See [PostgreSQL Store v1](../spec/POSTGRES_STORE_V1.md), [Guarantees](../spec/GUARANTEES.md), and [Roadmap](ROADMAP.md).
