# IdempotencyKit Roadmap

> Planned milestones, not release promises. Every phase is complete **only when its acceptance tests and evidence pass**.

## Phase 0 — Contract first (current)

- [x] Establish repository and cross-language project vision.
- [x] Draft README, HTTP contract, guarantees, architecture, and failure scenarios.
- [ ] Review and freeze key validation, request scoping and fingerprint algorithm.
- [ ] Decide open questions: JSON canonicalization, error schema, retention/replay policy, uncertain outcomes.
- [ ] Define machine-readable scenarios and golden test vectors for .NET + NestJS.
- [ ] Decide repository license and release/versioning policy.

**Exit:** a reviewable v1 protocol/spec with test fixtures and explicit exclusions.

## Scaffold milestone — two language workspaces

- [x] Add a native .NET 10 solution with core, ASP.NET Core, PostgreSQL, unit-test and sample projects.
- [x] Add an npm workspace with NestJS 12, core, NestJS adapter, PostgreSQL adapter and a sample API.
- [x] Add initial scaffold tests and GitHub Actions workflow.
- [ ] Verify that both ecosystems build and tests pass on CI; fix issues and record results.
- [ ] Generate and commit npm lockfile; migrate CI from npm install to npm ci.

The code is **scaffold-only**, with no actual HTTP idempotency enforcement.

## Phase 1 — .NET Standard mode

- [ ] Create .NET solution, core contracts, ASP.NET Core adapter, and PostgreSQL store.
- [ ] Implement atomic claims with unique indexes and owner fencing.
- [ ] Implement request scope, fingerprints, result serialization, safe replay, mismatch, and in-flight behavior.
- [ ] Handle lease expiry conservatively (uncertain/reconcile).
- [ ] Run real PostgreSQL concurrency tests and Minimal API + MVC integration tests.
- [ ] Document public APIs using a runnable sample, not pseudocode.

**Exit:** repeatable Standard-mode tests and honest documentation of crash gap.

## Phase 2 — NestJS Standard mode

- [ ] Build npm workspace, core, NestJS interceptor/decorator/module, PostgreSQL adapter.
- [ ] Ensure auth/guard ordering and Express/Fastify behavior are explicit.
- [ ] Consume shared contract fixtures and parity scenarios.
- [ ] Test multi-replica duplicates, malformed JSON, body canonicalization, response replay.

**Exit:** identical observable behavior for supported HTTP routes on both platforms.

## Phase 3 — Transactional mode

- [ ] Define explicit transaction-aware operation APIs in both languages.
- [ ] Implement EF Core/PostgreSQL shared transaction integration.
- [ ] Implement one selected NestJS PostgreSQL transaction integration.
- [ ] Serialize result before commit; ensure database transaction rollback/commit covers business + record.
- [ ] Demonstrate real process-kill before/after commit and HTTP-retry outcome.

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
