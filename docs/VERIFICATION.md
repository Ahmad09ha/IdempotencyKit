# Verification Evidence — IdempotencyKit

This file records **observed test results**, separately from planned guarantees. A test passing on a simulated failure or single CI host is **not** equivalent to production failure certification.

## 2026-11-23 — PostgreSQL Transactional Mode v1

**Verified source commit:** `c7d03838925af67c4c407df3440c19aad4be9777` on `feature/transactional-postgres-v1`.  
**GitHub Actions:** [Build and PostgreSQL Integration CI — run 45239007745](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45239007745). Both jobs completed successfully.

| Ecosystem | CI runtime | Tests passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: |
| .NET 10 / Npgsql | GitHub Actions Ubuntu + PostgreSQL 17 | **28** | 0 | 0 |
| Node.js 24 / TypeScript / NestJS 12 | GitHub Actions Ubuntu + PostgreSQL 17 | **25** | 0 | 0 |
| **Total** | | **53** | **0** | **0** |

The .NET total comprises 7 core tests, 12 PostgreSQL store/transactional tests, and 9 ASP.NET Core HTTP integration tests. The Node total comprises shared fingerprint tests, Standard-mode store and NestJS HTTP tests, and 7 additional PostgreSQL Transactional-mode tests.

### Database transactional scenarios actually exercised

- Callback writes a test order and a replay response in the same PostgreSQL transaction.
- Matching completed requests replay without reexecuting the business callback.
- Different fingerprints reject key reuse without business execution.
- Exceptions after order insertion force rollback of both the order row and idempotency row.
- Invalid response headers / invalid JSON are rejected **before commit** and roll back both rows.
- 24 concurrent same-key callback attempts produce **one committed business order** and one completed key; retries see the stored result.
- Existing Standard-mode processing/uncertain records prevent Transactional-mode callback execution.
- The Standard-mode store can replay a completed result written by Transactional mode.
- Deliberately terminating the **PostgreSQL backend** inside a still-uncommitted operation rolls back both rows, and a subsequent same-key call can execute successfully.
- Node-specific test: while the transaction is open, a second connection cannot observe the uncommitted records, and a bounded claim-lock wait returns `InProgress`.
- Node's PostgreSQL pool handles the expected delayed disconnect event from that backend-termination test without an unhandled exception.

### Earlier Standard-mode verification retained

- Canonical SHA-256 request hashing, cross-language JSON fixture parity, scoped identities.
- PostgreSQL Standard-mode atomic claim/owner fencing, lease extension, uncertain outcomes, response storage and cleanup.
- ASP.NET Core Minimal API and MVC protected routes, auth isolation, duplicate detection and replay.
- NestJS Express guarded routes, raw-body fingerprinting, in-progress 409, mismatched 422, and completed-response replay.

### Known verification gaps (not yet asserted)

- **No API-process SIGKILL** at before/after COMMIT checkpoints, with HTTP reconnect to restarted replicas.
- **No failover or asynchronous replication loss** injection; PostgreSQL primary durability settings matter.
- No true multi-host independent process stress/benchmark of Transactional mode (the 24-way concurrency tests use separate pooled connections).
- No EF Core `DbContext` transaction adapter verified against Npgsql.
- No Transactional-mode HTTP attribute/decorator that automatically creates the shared business transaction.
- No external payment/email/queue exactly-once guarantees, no Fastify adapter, no released NuGet/npm packages, no production deployment/security audit.
- .NET HTTP test host currently logs framework obsolete-API warnings; these warnings do not indicate a failed build/test.

**Conclusion:** the database callback APIs have verified atomicity for supported PostgreSQL SQL operations in the tested cases. Do not upgrade this evidence into network-level or external side-effect exactly-once claims.

See [Transactional Mode v1](../spec/TRANSACTIONAL_MODE_V1.md), [Guarantees](../spec/GUARANTEES.md), and [Failure Matrix](../spec/FAILURE_MATRIX.md).
