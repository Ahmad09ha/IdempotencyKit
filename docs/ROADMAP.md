# IdempotencyKit Roadmap

> Planned milestones, not release promises. Every phase is complete **only when its acceptance tests and evidence pass**.

## Phase 0 — Contract first (current)

- [x] Establish repository and cross-language project vision.
- [x] Draft README, HTTP contract, guarantees, architecture, and failure scenarios.
- [ ] Review and freeze key validation, request scoping and fingerprint algorithm.
- [ ] Decide open questions: JSON canonicalization, error schema, retention/replay policy, uncertain outcomes.
- [x] Add shared golden vectors for scoped identity and request fingerprinting; run both languages against them.
- [ ] Decide repository license and release/versioning policy.

**Exit:** a reviewable v1 protocol/spec with test fixtures and explicit exclusions.

## Scaffold milestone — two language workspaces

- [x] Add a native .NET 10 solution with core, ASP.NET Core, PostgreSQL, unit-test and sample projects.
- [x] Add an npm workspace with NestJS 12, core, NestJS adapter, PostgreSQL adapter and a sample API.
- [x] Add initial scaffold tests and GitHub Actions workflow.
- [x] Verify the initial scaffold builds and tests in GitHub Actions (run 45134472942); revalidate new hashing code in the feature PR.
- [ ] Generate and commit npm lockfile; migrate CI from npm install to npm ci.

The core implements scoped fingerprints and the PostgreSQL adapters implement Standard-mode database coordination and persisted response data. Both frameworks now implement Standard-mode HTTP integration. An explicit PostgreSQL **same-transaction business callback** now exists in both languages, but there is **no EF Core adapter, transactional HTTP decorator, publication, or automated reconciliation**.

## Phase 1 — .NET Standard mode

- [x] Create .NET solution, core contracts, ASP.NET Core adapter scaffold, and PostgreSQL Standard-mode store.
- [x] Implement PostgreSQL atomic claims with unique indexes and owner fencing.
- [x] Implement scoped fingerprints, raw JSON response storage, mismatch detection, and in-flight store behavior.
- [x] Implement ASP.NET Core JSON response capture and replay via metadata + pre-model-binding middleware.
- [x] Handle lease expiry conservatively (mark uncertain; no blind reclaim).
- [ ] Add controlled operational reconciliation interface and policies.
- [x] Add real PostgreSQL concurrency/expiry/replay integration tests.
- [x] Add Minimal API and MVC HTTP integration tests against real PostgreSQL.
- [ ] Document public APIs using a runnable sample, not pseudocode.

**Exit:** repeatable Standard-mode tests and honest documentation of crash gap.

## Phase 2 — NestJS Standard mode

- [x] Build npm workspace, TypeScript core and PostgreSQL Standard-mode adapter.
- [x] Implement NestJS module, `@Idempotent` decorator, and globally registered interceptor.
- [x] Verify authenticated guard ordering and raw-body fingerprinting using NestJS Express.
- [ ] Add and verify a separate Fastify adapter before promising Fastify support.
- [ ] Consume shared contract fixtures and parity scenarios.
- [x] Add NestJS Express HTTP tests for replay, conflicts, scope isolation and unknown outcomes.
- [ ] Add process-crash, multi-replica and wire-response serializer parity tests.

**Exit:** identical observable behavior for supported HTTP routes on both platforms.

## Phase 3 — Transactional mode

- [x] Define and implement explicit Npgsql and node-postgres transaction-aware callbacks.
- [ ] Implement dedicated EF Core/PostgreSQL shared transaction adapter (not yet supported as a packaged feature).
- [x] Implement PostgreSQL `pg.PoolClient` callback usable from NestJS services, with one owned BEGIN/COMMIT/ROLLBACK transaction.
- [x] Validate/serialize supported JSON response before COMMIT; exercise business-row + idempotency-row atomicity against real PostgreSQL.
- [x] Simulate database-backend termination before COMMIT and verify both rows roll back; verify completed retry replay.
- [ ] Demonstrate real API-process SIGKILL before/after commit and HTTP retry across restarted hosts.

**Exit:** one committed business effect and replayable result in verified failure windows.

## Phase 4 — Hardening and observability

- [ ] Add fault injection, 100+ concurrent requests, two running API replicas, storage outages.
- [ ] Instrument claims, replays, conflicts, mismatches, uncertainty, latency and retention.
- [ ] Security tests for tenant isolation, auth replay, secret/body leakage and unsafe headers.
- [ ] Operational cleanup/retention rules and reconciliation procedures.
- [ ] Add Redis Standard-mode adapter only after documenting eviction/failover limitations.
- [ ] Produce reproducible CI and `docs/VERIFICATION.md` with dated evidence.

**Exit:** documentation matches tested environments, not theoretical behavior.

## Phase 5 — Distribution

- [ ] NuGet packaging with supported target frameworks, XML docs and package metadata.
- [ ] npm packaging with type declarations, module compatibility and documented peer dependencies.
- [ ] Examples, migration strategy, semantic versioning, changelog, release automation.
- [ ] Public release only after explicit safety review and validation.

## Suggested initial choices

- PostgreSQL first; defer Redis.
- JSON REST first; defer streaming, multipart uploads, and manual responses.
- .NET and NestJS share **testable behavior**, not copied runtime code.
- Start with a single vertical slice: create order -> replay, then concurrency -> crash.
- Use Failure Matrix as the backlog of evidence.

## What "done" must mean

Passing unit tests alone is insufficient. Each released guarantee requires real-database tests and an executable example, with known limitations described in the README and versioned verification results. A design document is **not** production certification.
