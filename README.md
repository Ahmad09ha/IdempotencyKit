# IdempotencyKit

**One HTTP idempotency contract. Two native implementations: ASP.NET Core and NestJS.**

> **Project status:** Standard-mode HTTP, PostgreSQL/SQL Server 2022/SQLite stores and explicit same-database Transactional APIs, EF Core 10 bridges, and provider-specific ASP.NET Core and NestJS Express endpoints are implemented. Both Orders samples now run a real protected SQLite write. The preview build prepares 11 NuGet and eight npm packages; none have been published. See [getting started](docs/GETTING_STARTED.md), [شرح المشروع بالعربي](docs/WALKTHROUGH_AR.md), [operations](docs/OPERATIONS.md) and [release instructions](docs/RELEASING.md).

IdempotencyKit aims to make client retries safer for write APIs. It will detect repeated logical requests, reject conflicting reuse of a key, and replay recorded responses without accidentally executing the same handler twice. For business writes that need stronger crash protection, it will provide an explicitly **transactional** execution API that commits the business mutation and replayable result in the **same database transaction**.

The goal is not “exactly once over the network.” The goal is precise, testable guarantees, with honest failure boundaries.

## Why build this?

Imagine an order-creation endpoint:

~~~http
POST /api/orders HTTP/1.1
Content-Type: application/json
Idempotency-Key: order-abc-123

{"productId":42,"quantity":2}
~~~

The server creates order `#1001` and commits the database transaction. The HTTP connection then breaks **before the client receives the response**. The client retries. Without protection, the retry may create order `#1002`.

An idempotency-aware endpoint can instead use the same client-supplied key to recognize the logical operation, and, once safely completed, replay the original `201 Created` response for order `#1001`.

There is an important catch: **“the handler ran” and “the result was durably recorded” are not necessarily atomic.** If the server commits an order but crashes before storing the result, ordinary middleware may be unable to determine whether retrying is safe. This project treats that crash window as a first-class engineering problem.

## Workspace packages and provider strategy

All current packages are **source workspaces, not published NuGet/npm artifacts**.

| Package | Status | Purpose |
| --- | --- | --- |
| `IdempotencyKit.Core` / `@idempotencykit/core` | Implemented | Shared scoped SHA-256 identity, Standard-mode store contract and generic Transactional-mode executor contract |
| `IdempotencyKit.AspNetCore` / `@idempotencykit/nestjs` | Implemented | Standard-mode HTTP integration |
| `IdempotencyKit.Postgres` / `@idempotencykit/postgres` | Implemented | PostgreSQL Standard-mode store and Transactional-mode executor |
| `IdempotencyKit.EntityFrameworkCore.Postgres` | Implemented | EF Core 10 with one shared PostgreSQL transaction |
| `IdempotencyKit.AspNetCore.Postgres` / `@idempotencykit/nestjs-postgres` | Implemented | Explicit PostgreSQL transactional HTTP endpoints |
| `IdempotencyKit.SqlServer` / `@idempotencykit/sqlserver` | **Implemented** | SQL Server 2022 Standard-mode store and same-transaction executor |
| `IdempotencyKit.EntityFrameworkCore.SqlServer` | **Implemented** | EF Core 10 shared SQL Server connection + transaction bridge |
| `IdempotencyKit.AspNetCore.SqlServer` / `@idempotencykit/nestjs-sqlserver` | **Implemented** | Explicit authenticated transactional JSON endpoints for SQL Server |
| `IdempotencyKit.Sqlite` / `@idempotencykit/sqlite` | **Implemented** | Local on-disk WAL/FULL Standard + Transactional core |
| `IdempotencyKit.EntityFrameworkCore.Sqlite` | **Implemented** | EF Core 10 on exact SQLite connection/transaction |
| `IdempotencyKit.AspNetCore.Sqlite` / `@idempotencykit/nestjs-sqlite` | **Implemented** | Explicit authenticated transactional HTTP for ASP.NET Core and NestJS Express |

The shared contract is **behavioral**, not shared database SQL. Different engines need different locking, transaction APIs, migrations and failure tests. See [Provider Architecture v1](spec/PROVIDER_ARCHITECTURE_V1.md) and [Database Provider Rollout](docs/DATABASE_PROVIDER_ROADMAP.md).

## Two explicitly different protection modes

| | Standard mode | Transactional mode |
| --- | --- | --- |
| Developer experience | Opt-in endpoint integration | Explicit transaction-aware operation |
| Same-key concurrent requests | Atomic claim prevents simultaneous active owners while the lease is valid | Database unique constraint and transaction coordination |
| Completed response replay | Yes, when the result was durably stored | Yes, when result and business mutation committed together |
| Crash after business commit, before recording result | **Potentially uncertain**; requires reconciliation or an independent business-level key | Protected only for supported business writes in the **same transaction** |
| External HTTP calls, emails, payments | Not exactly-once | Not exactly-once |
| Planned initial storage | PostgreSQL | PostgreSQL |
| Later storage option | Redis adapter (with documented durability/eviction limitations) | Database/ORM-specific adapters only |

**Standard mode:** an idempotency record is claimed before execution. A stable ownership token, expiry/lease, and atomic state changes coordinate duplicate requests. A lease expiring does **not** prove the previous business effect failed. The default recovery policy will be conservative: return an uncertain outcome for reconciliation rather than blindly run the operation again.

**Transactional mode:** inside one real PostgreSQL transaction, claim the scoped key, perform database writes, prepare the supported response representation, mark the request complete, and commit. If the transaction rolls back, no completed result or business changes remain. If it commits and the HTTP response is lost, the result can be replayed. Framework decorators alone cannot provide this guarantee; explicit transaction integration is required.

Read the precise promises and exclusions in [Guarantees](spec/GUARANTEES.md) and [Architecture](docs/ARCHITECTURE.md).

## Proposed HTTP contract

Clients send an `Idempotency-Key` header for protected write operations; they **reuse the same key for retries of the same logical operation** and use a **new key** for a new intended operation.

| Situation | Proposed response |
| --- | --- |
| New valid scoped key | Execute once as permitted and persist the supported result |
| Completed key + same request fingerprint | Replay previously stored status/body and allowlisted headers |
| Same key + different fingerprint | `422 Unprocessable Content` |
| Same key + currently in-progress operation | `409 Conflict`; include a retry hint when appropriate |
| Missing or malformed key on an endpoint requiring it | `400 Bad Request` |
| Required idempotency storage unavailable | `503 Service Unavailable` (fail closed) |
| Expired lease with unproven business outcome in Standard mode | Controlled uncertain/reconciliation response; **not** automatic unsafe re-execution |

The lookup identity must include the **service namespace, stable operation ID, authenticated principal/tenant scope, and client-provided key**, not the client key alone. Request fingerprints must consistently cover relevant method, route, query, body, and API-version information. Authentication and authorization must happen before replaying cached data.

See [HTTP Contract](spec/HTTP_CONTRACT.md) for the protocol, response handling, and open implementation decisions.


## Use the implemented Standard-mode HTTP APIs

**ASP.NET Core:** register `IIdempotencyStore` backed by `PostgresIdempotencyStore`, a trusted `ResolveCaller` function, and the middleware after authentication/authorization:

~~~csharp
builder.Services.AddIdempotencyKit(options =>
{
    options.Namespace = "orders-api";
    options.ResolveCaller = ctx => new IdempotencyCallerScope(
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value,
        ctx.User.FindFirst("tenant_id")?.Value);
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotencyKit();

app.MapPost("/orders", CreateOrder)
   .RequireAuthorization()
   .WithIdempotency("orders.create.v1");
~~~

MVC controllers use `[Idempotent("orders.create.v1")]` on authorized actions. **Implementation note:** .NET uses endpoint metadata plus **middleware before model binding**, not a pure endpoint filter, so it can fingerprint the original JSON.

**NestJS (Express):** register `IdempotencyKitModule.forRoot(...)` with the store and a trusted guard-based `resolveCaller`, then:

~~~typescript
const app = await NestFactory.create(AppModule, { rawBody: true });

// On a controller protected with your real authentication guard:
@Post()
@Idempotent("orders.create.v1")
create(@Body() dto: CreateOrderDto) {
  return this.orders.create(dto);
}
~~~

The snippets show actual API names but omit the consuming app's authentication, DI and business-service setup. **Neither package is published yet.** For complete registration, integration tests, limits and recovery guarantees, see [HTTP Standard Mode v1](docs/HTTP_STANDARD_V1.md).

## Architecture (Standard HTTP and explicit PostgreSQL Transactional Mode implemented)

~~~text
ASP.NET Core metadata + middleware         NestJS decorator / interceptor
                   |                                      |
             .NET core engine                     TypeScript core engine
                   |                                      |
             PostgreSQL store                    PostgreSQL store
                   |                                      |
        transactional coordinator             transactional coordinator
                   \                                      /
                    shared HTTP + failure specification
                         and parity test fixtures
~~~

Key components are: an atomic idempotency store, scoped key resolver, deterministic request fingerprinting, response serializer, lease ownership and recovery logic, and a transaction coordinator.

## Failure-driven development

The acceptance suite targets the following properties. See the dated evidence for exact test scenarios and remaining limits:

- 100 simultaneous requests with the same key cannot produce multiple **transactional** business effects.
- Same key and different payload returns `422` without executing business logic.
- A completed request returns the same supported response on retry.
- A process killed before transaction commit leaves neither order nor completed idempotency record.
- A process killed after commit but before HTTP response leaves a replayable result.
- Two API replicas sharing PostgreSQL coordinate the same key.
- Expired leases, stale owners, storage outages, and key-retention boundaries behave as documented.
- One user's key never replays another user's response.
- .NET and NestJS satisfy the **same language-neutral scenarios**.

See [Failure Matrix](spec/FAILURE_MATRIX.md). See [Verification Evidence](docs/VERIFICATION.md) for which cases were exercised versus planned.

## Repository organization

~~~text
IdempotencyKit/
├── spec/              # SHA-256 contract, failure matrix, provider design and fixtures
├── docs/              # integration guides, rollout roadmap, verified test evidence
├── dotnet/
│   ├── src/           # core, three providers, EF Core and ASP.NET Core packages
│   ├── tests/         # unit and real-database integration tests
│   └── samples/       # runnable SQLite Orders sample
├── typescript/
│   ├── packages/      # core, three providers, NestJS Standard + Transactional adapters
│   ├── tests/         # HTTP, concurrency, crash, and fixture tests
│   └── apps/          # runnable SQLite Orders sample
└── .github/workflows/ # provider and no-publish packaging workflows
~~~

The SQL Server provider now has its own `spec/sql/sqlserver`, `.NET` and TypeScript package directories and a dedicated SQL Server CI workflow. SQLite now has its own database-core packages, versioned SQL migration and dedicated file-backed CI workflow.

## Build and test the workspaces

> **Preview, not production-certified.** The standalone SQLite examples need no database server. Set the documented environment variables to use real PostgreSQL or SQL Server in their integration suites.

**.NET 10 SDK** (from the repository root):

~~~bash
cd dotnet
dotnet restore IdempotencyKit.slnx
dotnet build IdempotencyKit.slnx --configuration Release
dotnet test IdempotencyKit.slnx --configuration Release
dotnet run --project samples/Orders.AspNetCore
~~~

**NestJS 12 / TypeScript 6**, with a current **Node 24 LTS** and npm:

~~~bash
cd typescript
npm ci
npm run build
npm test
npm run start:orders
~~~

The .NET Orders sample defaults to `127.0.0.1:5050`, and the NestJS sample to `127.0.0.1:3000`. Both have `POST /orders` and a safe local-only demo token. Run `node typescript/apps/orders-api/smoke.mjs http://127.0.0.1:5050` from the repository root (or use port 3000) to create, replay and check that exactly one order was committed. See the [full demo commands](docs/GETTING_STARTED.md#run-the-orders-demos).

See [Getting Started](docs/GETTING_STARTED.md), [.NET workspace](dotnet/README.md), and [NestJS workspace](typescript/README.md).

## Implemented: request identity and fingerprint v1

The first core milestone now has actual code in both languages, plus shared golden test vectors under [`spec/test-vectors/fingerprint-v1.json`](spec/test-vectors/fingerprint-v1.json).

- **.NET:** `RequestIdentityHasher.ComputeScopeKey(...)` and `RequestFingerprinter.Compute(...)`
- **TypeScript:** `computeScopeKey(...)` and `computeRequestFingerprint(...)` from `@idempotencykit/core`
- Inputs include an authenticated scope, key, method, operation ID, path, decoded query pairs, and original JSON body text.
- SHA-256 results are prefixed `v1:`. The custom canonicalization format **IK-CJSON/1 is not RFC 8785/JCS**; consult [the exact algorithm and limits](spec/IDENTITY_AND_FINGERPRINT.md).

This deterministic fingerprinting is the foundation for all supported database providers. Fingerprints alone do not protect an HTTP endpoint: use the implemented Standard-mode or explicit Transactional-mode integrations with trusted authentication, authorization and supported response handling.

## Implemented: PostgreSQL Standard-mode store

**Migration (required):** [`spec/sql/postgres/001_idempotencykit_records.sql`](spec/sql/postgres/001_idempotencykit_records.sql). Execute it using your database migration pipeline before calling the store.

- **.NET:** [`PostgresIdempotencyStore`](dotnet/src/IdempotencyKit.Postgres/PostgresIdempotencyStore.cs), implementing `IIdempotencyStore` through Npgsql 10.
- **TypeScript:** [`PostgresIdempotencyStore`](typescript/packages/postgres/src/postgres-idempotency-store.ts), implementing the same contract through node-postgres.
- **Claim decisions:** `Acquired`, `InProgress`, `Completed`, `FingerprintMismatch`, `Uncertain`.
- **Owner fencing:** only a matching, unexpired owner can complete or extend a lease.
- **Response data:** bounded successful JSON response bytes and a strict safe-header allowlist.
- **Conservative expiry:** expired processing claims become `Uncertain`, never automatically reexecuted.
- **Cleanup:** explicit batch deletion of expired **completed** records only.

For the exact state machine, failure boundaries and API examples, read [PostgreSQL Store v1](spec/POSTGRES_STORE_V1.md).

**Warning:** Standard-mode HTTP protection still has a crash window if a business handler commits independently of its replay record. The explicit PostgreSQL Transactional Mode avoids that gap only for database commands correctly using its provided connection and transaction. Do not stack Standard-mode HTTP annotations over Transactional Mode for the same key.


## Implemented: HTTP Standard mode

- **ASP.NET Core:** `AddIdempotencyKit`, `UseIdempotencyKit`, `.WithIdempotency("operation.id")` and MVC `[Idempotent("operation.id")]`.
- **NestJS:** `IdempotencyKitModule.forRoot` and `@Idempotent("operation.id")` with a global, guard-aware interceptor and `rawBody: true`.
- **Both:** PostgreSQL claims, completed-response replay, 409 in-progress conflicts, 422 fingerprint mismatches, 400 required-key handling, and conservative uncertain outcomes.
- **End-to-end verification:** dedicated real PostgreSQL tests for HTTP handlers, not just mock storage tests.

Read [HTTP Standard Mode v1](docs/HTTP_STANDARD_V1.md) for actual application setup and important limitations.


## Implemented: shared transactional provider contracts

- **.NET:** `ITransactionalIdempotencyExecutor<TConnection,TTransaction>` and `TransactionalExecutionResult` in `IdempotencyKit.Core`. Each database supplies its own typed ADO.NET connection and transaction. The PostgreSQL executor implements the interface without breaking its existing concrete API.
- **TypeScript:** `TransactionalIdempotencyExecutor<TSession>`, `TransactionalExecutionStatuses` and result types in `@idempotencykit/core`. PostgreSQL implements `TransactionalIdempotencyExecutor<PoolClient>`.
- **Cross-language fixture:** [`transactional-provider-v1.json`](spec/fixtures/transactional-provider-v1.json) verifies identical outcome mapping. Real PostgreSQL tests verify the interface still commits business writes and replay results atomically.
- **What is not shared:** SQL syntax, lock acquisition, transaction ownership internals, schema migrations and topology limits.

See the [Provider Architecture v1](spec/PROVIDER_ARCHITECTURE_V1.md). **PostgreSQL, SQL Server and SQLite now have implemented core, EF Core and explicit transactional HTTP integrations. SQLite remains local-file/single-host only.**

## Implemented: SQLite Standard + Transactional database core

- **Schema and durability:** [SQLite v1 migration](spec/sql/sqlite/001_idempotencykit_records.sql), local on-disk WAL + synchronous FULL, explicit one-time initialization.
- **.NET 10:** `IdempotencyKit.Sqlite` provides `SqliteIdempotencyStore` and `SqliteTransactionalExecutor` using the supplied `SqliteConnection` + `SqliteTransaction` for the same-database SQL and replay record COMMIT.
- **Node 24:** `@idempotencykit/sqlite` uses built-in `node:sqlite`, a restricted statement session and an in-process per-file queue for async callbacks.
- **Concurrency:** `BEGIN IMMEDIATE` reserves SQLite's **single writer for the whole file**. An unrelated `SQLITE_BUSY` or `SQLITE_LOCKED` is a **storage error**, not evidence of a matching in-progress key.
- **Verification:** **24 passing real file-backed tests** (10 .NET + 14 Node), 100 concurrent same-key requests, conservative uncertain leases, replay, cleanup, rollback, and Node SIGKILL before and after COMMIT. [SQLite CI](https://github.com/Ahmad09ha/IdempotencyKit/actions/workflows/sqlite-ci.yml).
- **Limits:** one local host, local file only; no network-mounted WAL, multi-host distributed claims or atomic external payment/email effects.

**P4 now implemented:** `SqliteEfCoreTransactionalExecutor<TContext>`, ASP.NET Core `MapSqliteTransactionalPost` and NestJS Express `SqliteTransactionalHttpModule` / `SqliteTransactionalHttpService.handleExpress`. The SQLite-specific HTTP suite confirms that **unrelated writer contention is 503, not 409**, and actual NestJS process SIGKILL before/after COMMIT preserves safe same-key retries. See [SQLite EF Core + HTTP v1](docs/SQLITE_EFCORE_HTTP_V1.md) and [SQLite Store v1](docs/SQLITE_STORE_V1.md).

## Implemented: SQL Server Standard + Transactional core

- **Migration:** [SQL Server records schema](spec/sql/sqlserver/001_idempotencykit_records.sql), applied to the **same SQL Server database** as the business domain tables.
- **.NET:** `SqlServerIdempotencyStore` implements `IIdempotencyStore`; `SqlServerTransactionalExecutor` implements `ITransactionalIdempotencyExecutor<SqlConnection,SqlTransaction>`.
- **TypeScript:** `SqlServerIdempotencyStore` and `SqlServerTransactionalExecutor` (callback receives a `mssql.Transaction`). Use `new sql.Request(transaction)` for **all** business SQL.
- **Concurrency:** transaction-owned `sys.sp_getapplock` keyed by the **scoped SHA-256 hash**, plus a unique database key. A same-key lock timeout returns `InProgress` without a second callback; unexpected database errors fail closed.
- **Safety:** completed-result replay, mismatch rejection, Standard-mode owner/lease fencing, uncertain state, bounded JSON responses and one-transaction SQL business writes.
- **Tests:** real SQL Server 2022 CI for both languages, including 100 concurrent same-key transaction attempts. See [SQL Server Store v1](spec/SQLSERVER_STORE_V1.md) and [CI results](https://github.com/Ahmad09ha/IdempotencyKit/actions/workflows/sqlserver-ci.yml).

**Now included in separate provider-specific packages:** `SqlServerEfCoreTransactionalExecutor<TContext>`, ASP.NET Core `MapSqlServerTransactionalPost`, NestJS `SqlServerTransactionalHttpModule` / `SqlServerTransactionalHttpService.handleExpress`. See [SQL Server EF Core + HTTP v1](docs/SQLSERVER_EFCORE_HTTP_V1.md). **Verified on real SQL Server 2022:** full ASP.NET Core and NestJS process-crash recovery, and shared-database mixed-framework HTTP replay/contestation. **Still excluded:** Azure SQL failover certification, stable package publication and external-effect exactly-once claims. PostgreSQL integrations are unaffected.

## Implemented: PostgreSQL Transactional Mode v1

Both implementations now provide an **explicit atomic database operation API**, separate from the Standard-mode HTTP middleware/interceptor.

- **.NET / Npgsql:** `PostgresTransactionalExecutor.ExecuteAsync(scopeKeyHash, fingerprint, callback, retention, cancellationToken)`. The callback receives the shared `NpgsqlConnection` and `NpgsqlTransaction`.
- **TypeScript / node-postgres:** `PostgresTransactionalExecutor.execute(scopeKeyHash, fingerprint, callback, retentionMs)`. The callback receives the transaction's `pg.PoolClient`.
- Both create an **uncommitted unique-key reservation**, execute business SQL on the same transaction, validate/store the supported JSON response **before COMMIT**, and commit both together.
- Real PostgreSQL integration tests cover one committed business row under 24 simultaneous callers, rollback on handler/response failures, completed-result replay, Standard-mode interoperability, database-backend termination before commit, and separate Node worker-process SIGKILL before/after COMMIT.

**Transactional HTTP and EF Core are now available through explicit APIs:** ASP.NET Core `MapTransactionalPost`, NestJS `PostgresTransactionalHttpService.handleExpress`, and .NET `PostgresEfCoreTransactionalExecutor<TContext>`. Standard-mode `[Idempotent]` / `@Idempotent()` annotations **must not** wrap the same transactional operation.

Read [Transactional Mode v1](spec/TRANSACTIONAL_MODE_V1.md) and [Transactional HTTP + EF Core v1](docs/TRANSACTIONAL_HTTP_AND_EFCORE_V1.md) for setup, recovery requirements, and limitations. Both PostgreSQL and SQL Server now have verified **same-database** transactional implementations; neither guarantees "exactly once" for payments or other external effects.

## Development roadmap

1. Freeze initial invariants, HTTP/error contract, fingerprinting rules, and shared scenarios.
2. Build the ASP.NET Core Standard-mode engine and PostgreSQL adapter with real concurrency tests.
3. Build NestJS against the same contract and fixtures.
4. Add transaction-aware execution to both frameworks and test crash windows.
5. Expand stress/fault coverage, operations and observability; keep unverified environments out of support claims.
6. Verify publishable artifacts, then publish only after release approval and licensing.

Full acceptance criteria: [Roadmap](docs/ROADMAP.md). Learning topics and exercises: [Learning Guide](docs/LEARNING_GUIDE.md).

## Relationship with ReliableFlow

[**ReliableFlow**](https://github.com/Ahmad09ha/ReliableFlow) addresses reliable **asynchronous event publication/consumption** with an outbox/inbox pattern. IdempotencyKit addresses **incoming HTTP commands**. They complement rather than replace one another.

A future .NET sample may demonstrate one transaction containing **order creation + idempotency completion + ReliableFlow outbox event**, followed by inbox-protected event consumption. This requires deliberate shared transaction ownership and is **not implemented**.

## Boundaries and non-goals

- No claim of network-level or arbitrary external-side-effect “exactly once.”
- The first target is JSON REST writes; streaming, SSE, large file bodies, and manual/raw response writing are out of initial scope.
- Transactional mode covers supported business mutations executed in the **same durable database transaction**.
- Key deduplication is not a substitute for domain-level uniqueness, authentication, authorization, or payment-provider idempotency keys.
- Redis, other database engines, response encryption, full performance benchmarks and a production security audit remain future work. EF Core 10 and transactional HTTP tests run on PostgreSQL, SQL Server and local-file SQLite.
- A license has **not yet been selected**. Public visibility does not by itself grant open-source reuse rights.

## Design references

- [IETF HTTP Idempotency-Key Header draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) (draft status; details may evolve)
- [RFC 8785: JSON Canonicalization Scheme](https://www.rfc-editor.org/rfc/rfc8785)
- [RFC 9457: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457)
- [Stripe: Idempotent requests](https://docs.stripe.com/api/idempotent_requests)
- [ReliableFlow](https://github.com/Ahmad09ha/ReliableFlow)

**Release hardening status:** [Kestrel crash recovery](docs/RELATIONAL_HTTP_CRASH_LAB.md) and [mixed-runtime relational HTTP](docs/RELATIONAL_MIXED_RUNTIME_LAB.md) passed on PostgreSQL and SQL Server. SQLite's local-file process crashes, mixed-runtime, online backup/checkpoint, bounded page-cap SQLITE_FULL and [Linux permission faults](docs/SQLITE_FILESYSTEM_FAULT_LAB.md) have dedicated evidence. The integrated preview still requires exact-head regression and package checks. **Next:** security/performance review, remaining infrastructure fault/failover tests, and approved publication. Physical host disk exhaustion, broader production topologies, automatic reconciliation, license selection and stable release are not certified or completed.
