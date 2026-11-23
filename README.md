# IdempotencyKit

**One HTTP idempotency contract. Two native implementations: ASP.NET Core and NestJS.**

> **Project status: initial cross-framework scaffolds.** The .NET solution and NestJS/npm workspace now exist with core contract types, sample health endpoints, basic tests, and CI configuration. **The HTTP idempotency engine, PostgreSQL store, transactional protection, and claimed failure guarantees are not implemented or verified yet.** Nothing in the sample APIs protects write requests.

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

## Illustrative developer experience (not working code yet)

**ASP.NET Core (proposed):**

~~~csharp
builder.Services.AddIdempotencyKit(options =>
{
    options.Retention = TimeSpan.FromHours(24);
})
.UsePostgres(connectionString);

app.MapPost("/orders", CreateOrder)
   .RequireAuthorization()
   .WithIdempotency();
~~~

**NestJS (proposed):**

~~~typescript
@Module({
  imports: [
    IdempotencyKitModule.forRoot({
      storage: "postgres",
      retentionSeconds: 86400,
    }),
  ],
})
export class AppModule {}

@Post()
@Idempotent({ required: true, mode: "standard" })
create(@Body() dto: CreateOrderDto) {
  return this.orders.create(dto);
}
~~~

For **transactional** guarantees, both implementations will additionally expose an explicit operation API that accepts or creates a shared database transaction. The code above represents the **Standard** experience only. The actual APIs may evolve during implementation.

## Planned architecture

~~~text
ASP.NET Core endpoint filter / MVC filter       NestJS decorator / interceptor
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

## Run the scaffolds

> **Not production-ready**. These commands build the initial projects and run sample **health endpoints**; no idempotency protection is installed.

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

The NestJS sample serves `http://localhost:3000/health`. The .NET sample serves `/health` on the local URL printed by ASP.NET Core. Both report `scaffold-only`.

The initial TypeScript scaffold has no `package-lock.json` because registry access was unavailable during creation; commit a lockfile after a successful install and then switch CI to `npm ci`.

See [Getting Started](docs/GETTING_STARTED.md), [.NET workspace](dotnet/README.md), and [NestJS workspace](typescript/README.md).

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

**Next milestone:** review the shared protocol and request fingerprints, then implement the first atomic claim/replay feature in the scaffolded packages. Builds and tests are configured but not yet verified as passing.
