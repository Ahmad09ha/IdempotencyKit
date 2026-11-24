# IdempotencyKit Roadmap

> Planned milestones, not release promises. Every phase is complete **only when its acceptance tests and evidence pass**.

## Phase 0 — Contract first

- [x] Establish repository and cross-language project vision.
- [x] Draft README, HTTP contract, guarantees, architecture, and failure scenarios.
- [ ] Review and freeze key validation, request scoping and fingerprint algorithm.
- [ ] Decide open questions: JSON canonicalization, error schema, retention/replay policy, uncertain outcomes.
- [x] Add shared golden vectors for scoped identity and request fingerprinting; run both languages against them.
- [ ] Owner chooses a repository license and approves the public v1 contract (preview version and release process are documented).

**Exit:** a reviewable v1 protocol/spec with test fixtures and explicit exclusions.

## Scaffold milestone — two language workspaces

- [x] Add a native .NET 10 solution with core, ASP.NET Core, PostgreSQL, unit-test and sample projects.
- [x] Add an npm workspace with NestJS 12, core, NestJS adapter, PostgreSQL adapter and a sample API.
- [x] Add initial scaffold tests and GitHub Actions workflow.
- [x] Verify the initial scaffold builds and tests in GitHub Actions (run 45134472942); revalidate new hashing code in the feature PR.
- [x] Commit the npm lockfile and use `npm ci` in CI.

The core implements scoped fingerprints and the PostgreSQL adapters implement Standard-mode database coordination and persisted response data. Both frameworks now implement Standard-mode HTTP integration. An explicit PostgreSQL same-transaction callback now exists in both languages, with **EF Core 10/Npgsql support and explicit ASP.NET Core/NestJS transactional HTTP entry points**. SQL Server Standard + Transactional core adapters are now implemented in .NET and TypeScript, with a separate SQL Server 2022 test workflow. SQL Server EF Core 10 and explicit ASP.NET Core/NestJS Express transactional HTTP are now implemented and tested against real SQL Server 2022, including full NestJS HTTP-process SIGKILL before/after COMMIT. SQLite file-backed Standard/Transactional core, EF Core 10 and explicit ASP.NET Core/NestJS Express transactional HTTP are verified in .NET and Node 24, including real NestJS HTTP process SIGKILL before/after COMMIT. Universal annotations, publication and automated reconciliation remain future work.

## Phase 1 — .NET Standard mode

- [x] Create .NET solution, core contracts, ASP.NET Core adapter scaffold, and PostgreSQL Standard-mode store.
- [x] Implement PostgreSQL atomic claims with unique indexes and owner fencing.
- [x] Implement scoped fingerprints, raw JSON response storage, mismatch detection, and in-flight store behavior.
- [x] Implement ASP.NET Core JSON response capture and replay via metadata + pre-model-binding middleware.
- [x] Handle lease expiry conservatively (mark uncertain; no blind reclaim).
- [ ] Add controlled operational reconciliation interface and policies.
- [x] Add real PostgreSQL concurrency/expiry/replay integration tests.
- [x] Add Minimal API and MVC HTTP integration tests against real PostgreSQL.
- [x] Provide runnable protected Orders sample and Standard/Transactional integration documentation.

**Exit:** repeatable Standard-mode tests and honest documentation of crash gap.

## Phase 2 — NestJS Standard mode

- [x] Build npm workspace, TypeScript core and PostgreSQL Standard-mode adapter.
- [x] Implement NestJS module, `@Idempotent` decorator, and globally registered interceptor.
- [x] Verify authenticated guard ordering and raw-body fingerprinting using NestJS Express.
- [ ] Add and verify a separate Fastify adapter before promising Fastify support.
- [x] Exercise shared fingerprint and transactional outcome fixtures in both ecosystems.
- [x] Add NestJS Express HTTP tests for replay, conflicts, scope isolation and unknown outcomes.
- [ ] Add process-crash, multi-replica and wire-response serializer parity tests.

**Exit:** identical observable behavior for supported HTTP routes on both platforms.

## Phase 3 — Transactional mode

- [x] Define and implement explicit Npgsql and node-postgres transaction-aware callbacks.
- [x] Implement EF Core 10/PostgreSQL shared-connection adapter that verifies the exact connection, enlists `UseTransactionAsync` and saves tracked changes before COMMIT.
- [x] Implement PostgreSQL `pg.PoolClient` callback usable from NestJS services, with one owned BEGIN/COMMIT/ROLLBACK transaction.
- [x] Validate/serialize supported JSON response before COMMIT; exercise business-row + idempotency-row atomicity against real PostgreSQL.
- [x] Simulate database-backend termination before COMMIT and verify both rows roll back; verify completed retry replay.
- [x] Demonstrate real forked Node worker-process SIGKILL before and after COMMIT, validating database rollback and replay.
- [x] Demonstrate live NestJS Express HTTP server SIGKILL before and after COMMIT, with a same-key retry through a surviving test host.
- [ ] Build equivalent full ASP.NET Core server-process SIGKILL/restart scenarios.

**Exit:** one committed business effect and replayable result in verified failure windows.

## Next planning milestone — more database providers

- [x] Define provider-neutral .NET/TypeScript transactional interfaces, keep Standard-mode store contracts, and implement PostgreSQL adapters against them.
- [x] Document SQL Server 2022 locking and migration semantics, with Azure SQL as an unverified candidate.
- [x] Document SQLite WAL, BEGIN IMMEDIATE, busy-timeout, file durability, single-host topology and real test gates.
- [x] Document MySQL/MariaDB and Redis evaluation and acceptance gates in [Next Providers](NEXT_PROVIDERS.md).
- [ ] Implement and certify additional providers if separately prioritized.
- [x] Version provider-specific migrations and run independent real-database tests for the three supported providers.

**Full staged plan:** [Database Provider Rollout](DATABASE_PROVIDER_ROADMAP.md) and [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md).

## SQL Server core implementation (P1)

- [x] Implement separate SQL Server 2022 schema migration with unique scoped key, safe JSON replay fields and UTC retention.
- [x] Implement `SqlServerIdempotencyStore` and `SqlServerTransactionalExecutor` for .NET with `Microsoft.Data.SqlClient`.
- [x] Implement equivalent `@idempotencykit/sqlserver` Standard + Transactional stores through `mssql`.
- [x] Use transaction-owned, same-key `sp_getapplock` across both modes and both language implementations.
- [x] Add real SQL Server 2022 CI for both ecosystems, preserving independent PostgreSQL CI.
- [x] Add regression tests for owner fencing, mismatch, replay, rollback, uncertain outcomes, cleanup and 100-way concurrency.
- [x] Add SQL Server EF Core 10 shared-transaction bridge, ASP.NET Core `MapSqlServerTransactionalPost`, and NestJS Express `SqlServerTransactionalHttpService.handleExpress`.
- [x] Test all three SQL Server integration APIs against real SQL Server 2022, including Node HTTP-process SIGKILL before and after COMMIT.
- [ ] Add equivalent ASP.NET Core HTTP process SIGKILL before/after COMMIT lab, Azure SQL failover verification, and publication hardening.
- [ ] Run Azure SQL-specific durability/failover and full HTTP process-crash suites before supporting those production claims.

## SQLite core implementation (P3)

- [x] Add SQLite on-disk migration with unique scoped hash, WAL and synchronous FULL.
- [x] Implement .NET `IdempotencyKit.Sqlite` Standard store and shared-transaction executor.
- [x] Implement Node 24 `@idempotencykit/sqlite` with guarded statement session and per-file local queue.
- [x] Enforce fail-closed unrelated `SQLITE_BUSY` rather than false `InProgress`; never use in-memory tests to claim durable behavior.
- [x] Validate owner fencing, mismatch, replay, uncertain leases, cleanup, atomic rollback and 100-way same-key concurrency on actual files in both ecosystems.
- [x] Test cross-process writer contention and Node SIGKILL before/after COMMIT with same-key retries.
- [x] SQLite EF Core 10, authenticated ASP.NET Core and NestJS Express transactional HTTP adapters (P4).
- [x] Verify SQLite online backup/restore, pinned-reader checkpoint behavior and rollback under a constrained SQLite page cap.
- [ ] Verify actual host disk exhaustion, file permissions and production storage under failure.
- [ ] Release packaging, operations, incident reconciliation and security reviews.

## SQLite EF Core 10 + transactional HTTP integration (P4)

- [x] Add `SqliteEfCoreTransactionalExecutor<TContext>` with exact SqliteConnection identity validation and `UseTransactionAsync`, no ambient/magic enlistment.
- [x] Verify EF Core generated keys and queued tracked changes, mismatch/replay, rollback after SaveChangesAsync, wrong-connection failure and concurrent same-key attempts.
- [x] Add `AddSqliteTransactionalIdempotency` and `MapSqliteTransactionalPost` for explicitly authenticated ASP.NET Core JSON operations, with current permission checks before replay.
- [x] Verify two ASP.NET Core HTTP hosts on the same file, 401/403, 400/422, known-record 409 and unrelated file-writer 503, rollback and 16 concurrent same-key calls.
- [x] Add `@idempotencykit/nestjs-sqlite` for NestJS Express 12 / Node 24 rawBody transaction callbacks with guards and fresh authorization.
- [x] Verify live NestJS SQLite HTTP server process `SIGKILL` before COMMIT (rollback/retry) and after COMMIT but before reply (durable replay).
- [x] Keep original P3 SQLite real-file tests and PostgreSQL/SQL Server regression CI green.
- [x] Full .NET SQLite API-process SIGKILL, mixed-language same-file SQLite tests, and online backup/restore lab.
- [ ] Production audit and separately designed automatic incident reconciliation (no blind retry).

See [SQLite EF Core + HTTP v1](SQLITE_EFCORE_HTTP_V1.md).

## Phase 4 — Hardening and observability

- [ ] Add fault injection, 100+ concurrent requests, two running API replicas, storage outages.
- [x] Instrument bounded Standard/Transactional HTTP outcomes and latency; document retention/cleanup monitoring.
- [ ] Instrument provider-core callers and retention worker metrics if introduced.
- [x] Verify tenant isolation, authorization before replay, safe headers and bounded/no-body telemetry in supported HTTP suites.
- [ ] Independent production security review.
- [x] Document [retention, cleanup, outage and manual reconciliation](OPERATIONS.md); deliberately no unsafe automatic uncertain-state reset.
- [ ] Add Redis Standard-mode adapter only after documenting eviction/failover limitations.
- [x] Use a locked npm install, package preview CI, and dated [verification](VERIFICATION.md).

**Exit:** documentation matches tested environments, not theoretical behavior.

## Phase 5 — Distribution

- [x] NuGet preview artifacts with .NET 10, XML docs, metadata, provider migrations and standalone consumer smoke.
- [x] npm preview tarballs, Node 24 ESM, TypeScript declarations, documented NestJS peers and standalone consumer smoke.
- [x] Provide runnable examples, versioned migrations, preview SemVer/changelog and no-publish package automation.
- [ ] Configure trusted publishing after maintainer approval and package/scope ownership checks.
- [ ] Public release only after explicit safety review and validation.

## Suggested initial choices

- PostgreSQL first; defer Redis.
- JSON REST first; defer streaming, multipart uploads, and manual responses.
- .NET and NestJS share **testable behavior**, not copied runtime code.
- Start with a single vertical slice: create order -> replay, then concurrency -> crash.
- Use Failure Matrix as the backlog of evidence.

## What "done" must mean

Passing unit tests alone is insufficient. Each released guarantee requires real-database tests and an executable example, with known limitations described in the README and versioned verification results. A design document is **not** production certification.
