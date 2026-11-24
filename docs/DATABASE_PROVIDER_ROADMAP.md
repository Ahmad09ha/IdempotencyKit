# Database provider rollout plan

**Planning baseline:** PostgreSQL Standard + Transactional mode, EF Core Npgsql, ASP.NET Core and NestJS Express HTTP integration are already implemented and tested. Shared provider-neutral transactional interfaces are now being added. **No SQL Server or SQLite production adapter is implemented yet.**

## Recommended sequence

| Milestone | Scope | Exit criteria | Status |
| --- | --- | --- | --- |
| P0 — contracts | Generic .NET + TypeScript transactional interfaces, PostgreSQL compatibility, cross-language result fixture, architecture | Both existing PostgreSQL CI suites pass unchanged; new interface tests pass | Current PR |
| P1 — SQL Server core | Durable Standard-mode claim/store + explicit SQL Server transaction executor (.NET + TypeScript) | Real SQL Server CI, 100 concurrent same-key, mismatch, replay, rollback/commit and lock-timeout tests | Planned |
| P2 — SQL Server HTTP/EF | ASP.NET Core/Postgres-specific mapping equivalent, EF Core SQL Server integration, NestJS Express service | Real authenticated HTTP request + replay, SQL Server EF shared transaction, process crash before/after COMMIT | Planned |
| P3 — SQLite core | File-backed Standard-mode + transactional executor, explicit single-host deployment restrictions | Real **on-disk** SQLite tests for SQLITE_BUSY, WAL, multi-connection, replay, rollback and SIGKILL | Planned |
| P4 — SQLite HTTP/EF | EF Core SQLite and explicit ASP.NET Core / NestJS provider adapters where driver supports safe async lifetime | Same-key retry with exact response bytes after process restart, documented bounded writer contention | Planned |
| P5 — more engines | MySQL/MariaDB research, optional Redis Standard mode | Separate database-specific guarantees and verified CI evidence | Deferred |

## Package namespace policy

.NET:

~~~text
IdempotencyKit.Core                             # shared hashes + contracts
IdempotencyKit.AspNetCore                       # Standard-mode HTTP core
IdempotencyKit.Postgres                         # implemented
IdempotencyKit.EntityFrameworkCore.Postgres     # implemented
IdempotencyKit.AspNetCore.Postgres               # implemented
IdempotencyKit.SqlServer                        # planned
IdempotencyKit.EntityFrameworkCore.SqlServer    # planned
IdempotencyKit.AspNetCore.SqlServer              # planned
IdempotencyKit.Sqlite                           # planned
IdempotencyKit.EntityFrameworkCore.Sqlite       # planned
IdempotencyKit.AspNetCore.Sqlite                 # planned
~~~

TypeScript:

~~~text
@idempotencykit/core                  # shared hashes + generic contracts
@idempotencykit/postgres              # implemented
@idempotencykit/nestjs                # Standard-mode core
@idempotencykit/nestjs-postgres       # implemented
@idempotencykit/sqlserver             # planned
@idempotencykit/nestjs-sqlserver      # planned
@idempotencykit/sqlite                # planned (driver selection first)
@idempotencykit/nestjs-sqlite         # planned (depending on driver support)
~~~

**Planned paths are not directories or published packages yet.** The existing source workspaces are not released to NuGet/npm.

## P1 — SQL Server implementation checklist

- [ ] Pin versions and environment matrix: SQL Server 2022+/Azure SQL; decide local Docker CI image and licensing constraints.
- [ ] Design transactional claim using a unique key with verified lock acquisition; benchmark `sp_getapplock` versus key-range locking.
- [ ] Define exact handling of lock timeout, deadlock victim, cancellation, aborted transaction, duplicate key and unavailable primary.
- [ ] Add versioned T-SQL migration for record states, owner fencing, UTC times, replay bytes and safe headers.
- [ ] Implement `IIdempotencyStore` / `IdempotencyStore` with Standard-mode uncertain/recovery semantics.
- [ ] Implement `ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction>` and a Node transaction-session implementation.
- [ ] Run real SQL Server standard + transactional + 100-way concurrency tests and before/after COMMIT crash tests.
- [ ] Only then add EF Core SQL Server and explicit provider-specific HTTP adapters.
- [ ] Add verification matrix, troubleshooting and security/permissions documentation.

## P3 — SQLite implementation checklist

- [ ] Decide supported topology, local-file ownership, WAL/synchronous policy and Node driver lifecycle.
- [ ] Implement a file-backed migration with UNIQUE scoped hash and complete response bytes.
- [ ] Choose a safe `BEGIN IMMEDIATE` strategy for the provided transaction callback and bound busy timeout.
- [ ] Define `SQLITE_BUSY`: do not falsely report unrelated writers as same-key `InProgress`.
- [ ] Test separate processes/connections and file I/O errors in a temporary **on-disk** database; never treat `:memory:` test results as proof of durability.
- [ ] Test 100 concurrent requests (expect serialized writers and documented throughput limitations).
- [ ] Verify SIGKILL before/after COMMIT, same-key retry, file durability and operational backup/cleanup.
- [ ] Only then add EF Core SQLite and NestJS adapters, with topology restrictions prominently stated.

## Decisions to resolve before P1 code

1. Which SQL Server versions and hosting models are officially supported? Start with 2022+/Azure SQL CI, and expand only after testing.
2. Do we want a dedicated temporary storage-busy exception in core, or a provider-neutral categorized storage error? Until then, expose transport-level 503 rather than inventing a `409 InProgress` for unknown database writer contention.
3. How should an app reuse the provided transaction across its repositories/ORM? Start with explicit, provider-typed callback/session; never rely on ambient connection discovery.
4. Should rollback/commit ambiguity use a documented HTTP error code and retry policy shared with Standard mode? Preserve current fail-closed behavior until tested.
5. What are the retention and cleanup SLOs for each engine, and who runs scheduled cleanup?

These are engineering decisions, not questions the end user must answer before P0 can merge.

## Definition of done

Every provider needs a **real database** with schema migration, atomic claim correctness, explicit same-transaction business writes, cross-language result parity, contention handling, failure injection and observability. Production-grade support also needs durability and failover assumptions, authenticated HTTP tests, safe retention and deployment notes.

See [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md) and the [Verification Evidence](VERIFICATION.md).
