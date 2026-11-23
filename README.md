# IdempotencyKit

**One HTTP idempotency contract. Two native implementations: ASP.NET Core and NestJS.**

> **Project status: Standard-mode HTTP, PostgreSQL Transactional Mode, EF Core 10 shared-transaction support, and dedicated transaction-aware HTTP APIs are implemented.** ASP.NET Core and NestJS Express have explicit PostgreSQL transactional routes with real integration tests. **SQL Server/SQLite and other providers, universal transactional annotations, automated reconciliation, package publication, and production certification are not implemented.** The Orders demo apps remain health-only.

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

## The plan: two packages, one specification

| Planned artifact | Ecosystem | Purpose |
| --- | --- | --- |
| `IdempotencyKit.Core` | .NET / NuGet | Framework-independent core contracts and engine |
| `IdempotencyKit.AspNetCore` | ASP.NET Core / NuGet | Minimal API endpoint filter and MVC integration |
| `IdempotencyKit.Postgres` | .NET / NuGet | Durable PostgreSQL storage and transactional integration |
| `@idempotencykit/core` | TypeScript / npm | Framework-independent TypeScript contracts and engine |
| `@idempotencykit/nestjs` | NestJS / npm | Module, decorator, and interceptor integration |
| `@idempotencykit/postgres` | TypeScript / npm | Durable PostgreSQL store and transaction adapter |
| Shared specification + fixtures | Language neutral | Behavioral parity tests across both implementations |
| Failure lab | Docker + PostgreSQL | Multi-replica, concurrency, and process-crash experiments |

Names and package boundaries are **proposals** until the implementations exist. We intend to implement natively in both ecosystems rather than attempting to share .NET and TypeScript source code.

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

The acceptance suite should eventually prove:

- 100 simultaneous requests with the same key cannot produce multiple **transactional** business effects.
- Same key and different payload returns `422` without executing business logic.
- A completed request returns the same supported response on retry.
- A process killed before transaction commit leaves neither order nor completed idempotency record.
- A process killed after commit but before HTTP response leaves a replayable result.
- Two API replicas sharing PostgreSQL coordinate the same key.
- Expired leases, stale owners, storage outages, and key-retention boundaries behave as documented.
- One user's key never replays another user's response.
- .NET and NestJS satisfy the **same language-neutral scenarios**.

See [Failure Matrix](spec/FAILURE_MATRIX.md). The tests have **not** been implemented or executed yet.

## Proposed repository structure

The repository now contains the **actual initial .NET solution and NestJS/npm workspace**. Their implementations are scaffold-only; the following also shows directories reserved for future work:

~~~text
IdempotencyKit/
├── README.md
├── .gitignore
├── spec/
│   ├── HTTP_CONTRACT.md
│   ├── GUARANTEES.md
│   ├── FAILURE_MATRIX.md
│   └── test-vectors/        # planned executable fixtures
├── docs/
│   ├── ARCHITECTURE.md
│   ├── SECURITY_OPERATIONS.md
│   ├── ROADMAP.md
│   └── LEARNING_GUIDE.md
├── dotnet/                  # planned .NET solutions/packages
├── typescript/              # planned npm workspace/packages
├── samples/                 # planned equivalent Orders APIs
└── lab/                     # planned Docker/failure experiments
~~~

## Build and test the workspaces

> **Not production-ready**. The commands build both workspaces and run real PostgreSQL HTTP integration tests when the documented database environment variables are set. The standalone Orders sample apps remain health-only.

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
npm install
npm run build
npm test
npm run start:orders
~~~

The NestJS sample serves `http://localhost:3000/health`. The .NET sample serves `/health` on the local URL printed by ASP.NET Core. Both report `sample-health-only` because neither demo hosts a protected write endpoint by default.

The initial TypeScript scaffold has no `package-lock.json` because registry access was unavailable during creation; commit a lockfile after a successful install and then switch CI to `npm ci`.

See [Getting Started](docs/GETTING_STARTED.md), [.NET workspace](dotnet/README.md), and [NestJS workspace](typescript/README.md).

## Implemented: request identity and fingerprint v1

The first core milestone now has actual code in both languages, plus shared golden test vectors under [`spec/test-vectors/fingerprint-v1.json`](spec/test-vectors/fingerprint-v1.json).

- **.NET:** `RequestIdentityHasher.ComputeScopeKey(...)` and `RequestFingerprinter.Compute(...)`
- **TypeScript:** `computeScopeKey(...)` and `computeRequestFingerprint(...)` from `@idempotencykit/core`
- Inputs include an authenticated scope, key, method, operation ID, path, decoded query pairs, and original JSON body text.
- SHA-256 results are prefixed `v1:`. The custom canonicalization format **IK-CJSON/1 is not RFC 8785/JCS**; consult [the exact algorithm and limits](spec/IDENTITY_AND_FINGERPRINT.md).

This phase began with deterministic request comparison. The next step is an independently callable PostgreSQL Standard-mode storage API. **Neither the fingerprints nor the store alone protect an HTTP endpoint:** applications must later integrate the store with their authentication, business handlers, result serialization, and recovery policies.

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


## Implemented: PostgreSQL Transactional Mode v1

Both implementations now provide an **explicit atomic database operation API**, separate from the Standard-mode HTTP middleware/interceptor.

- **.NET / Npgsql:** `PostgresTransactionalExecutor.ExecuteAsync(scopeKeyHash, fingerprint, callback, retention, cancellationToken)`. The callback receives the shared `NpgsqlConnection` and `NpgsqlTransaction`.
- **TypeScript / node-postgres:** `PostgresTransactionalExecutor.execute(scopeKeyHash, fingerprint, callback, retentionMs)`. The callback receives the transaction's `pg.PoolClient`.
- Both create an **uncommitted unique-key reservation**, execute business SQL on the same transaction, validate/store the supported JSON response **before COMMIT**, and commit both together.
- Real PostgreSQL integration tests cover one committed business row under 24 simultaneous callers, rollback on handler/response failures, completed-result replay, Standard-mode interoperability, database-backend termination before commit, and separate Node worker-process SIGKILL before/after COMMIT.

**Transactional HTTP and EF Core are now available through explicit APIs:** ASP.NET Core `MapTransactionalPost`, NestJS `PostgresTransactionalHttpService.handleExpress`, and .NET `PostgresEfCoreTransactionalExecutor<TContext>`. Standard-mode `[Idempotent]` / `@Idempotent()` annotations **must not** wrap the same transactional operation.

Read [Transactional Mode v1](spec/TRANSACTIONAL_MODE_V1.md) and [Transactional HTTP + EF Core v1](docs/TRANSACTIONAL_HTTP_AND_EFCORE_V1.md) for setup, recovery requirements, and limitations. This is a **PostgreSQL-only, same-database** atomicity guarantee, not "exactly once" for payments or other external effects.

## Development roadmap

1. Freeze initial invariants, HTTP/error contract, fingerprinting rules, and shared scenarios.
2. Build the ASP.NET Core Standard-mode engine and PostgreSQL adapter with real concurrency tests.
3. Build NestJS against the same contract and fixtures.
4. Add transaction-aware execution to both frameworks and test crash windows.
5. Add multi-replica stress tests, observability, security hardening, and optional Redis support.
6. Run release verification, publish packages, and document real guarantees based on evidence.

Full acceptance criteria: [Roadmap](docs/ROADMAP.md). Learning topics and exercises: [Learning Guide](docs/LEARNING_GUIDE.md).

## Relationship with ReliableFlow

[**ReliableFlow**](https://github.com/Ahmad09ha/ReliableFlow) addresses reliable **asynchronous event publication/consumption** with an outbox/inbox pattern. IdempotencyKit addresses **incoming HTTP commands**. They complement rather than replace one another.

A future .NET sample may demonstrate one transaction containing **order creation + idempotency completion + ReliableFlow outbox event**, followed by inbox-protected event consumption. This requires deliberate shared transaction ownership and is **not implemented**.

## Boundaries and non-goals

- No claim of network-level or arbitrary external-side-effect “exactly once.”
- The first target is JSON REST writes; streaming, SSE, large file bodies, and manual/raw response writing are out of initial scope.
- Transactional mode covers supported business mutations executed in the **same durable database transaction**.
- Key deduplication is not a substitute for domain-level uniqueness, authentication, authorization, or payment-provider idempotency keys.
- Redis support, ORM adapters, response encryption, benchmarks, production audit, and CI verification are planned or under consideration; none should be assumed available.
- A license has **not yet been selected**. Public visibility does not by itself grant open-source reuse rights.

## Design references

- [IETF HTTP Idempotency-Key Header draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) (draft status; details may evolve)
- [RFC 8785: JSON Canonicalization Scheme](https://www.rfc-editor.org/rfc/rfc8785)
- [RFC 9457: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc9457)
- [Stripe: Idempotent requests](https://docs.stripe.com/api/idempotent_requests)
- [ReliableFlow](https://github.com/Ahmad09ha/ReliableFlow)

**Next milestone:** design the database-provider contract, then plan SQL Server and SQLite adapters with their own real-database transaction, concurrency and crash tests. Full .NET HTTP process-crash validation, error parity, reconciliation and publishable packages remain release goals.
