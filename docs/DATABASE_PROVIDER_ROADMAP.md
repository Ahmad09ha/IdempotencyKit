# Database provider rollout plan

**Planning baseline:** PostgreSQL Standard + Transactional mode, EF Core Npgsql, ASP.NET Core and NestJS Express HTTP integration are already implemented and tested. Shared provider-neutral transactional interfaces are now being added. **SQL Server Standard and Transactional core adapters are now implemented and tested against SQL Server 2022; SQL Server EF Core 10, ASP.NET Core and NestJS Express Transactional HTTP are also implemented and tested. SQLite adapters remain unimplemented.**

## Recommended sequence

| Milestone | Scope | Exit criteria | Status |
| --- | --- | --- | --- |
| P0 — contracts | Generic .NET + TypeScript transactional interfaces, PostgreSQL compatibility, cross-language result fixture, architecture | Both existing PostgreSQL CI suites pass unchanged; new interface tests pass | Current PR |
| P1 — SQL Server core | Durable Standard-mode claim/store + explicit SQL Server transaction executor (.NET + TypeScript) | Real SQL Server 2022 CI, 100 concurrent same-key, mismatch, replay, rollback and lock-timeout tests | **Implemented; CI verified** |
| P2 — SQL Server HTTP/EF | Explicit ASP.NET Core endpoint mapping, EF Core SQL Server integration, NestJS Express service | Real authenticated HTTP request/replay, SQL Server EF shared transaction, Node HTTP process SIGKILL before/after COMMIT | **Implemented; CI verified** |
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
IdempotencyKit.SqlServer                        # implemented core
IdempotencyKit.EntityFrameworkCore.SqlServer    # implemented
IdempotencyKit.AspNetCore.SqlServer              # implemented
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
@idempotencykit/sqlserver             # implemented core
@idempotencykit/nestjs-sqlserver      # implemented (Express)
@idempotencykit/sqlite                # planned (driver selection first)
@idempotencykit/nestjs-sqlite         # planned (depending on driver support)
~~~

**Core SQL Server and PostgreSQL paths exist but remain unpublished source packages.** All other planned paths are not directories or published packages yet. The existing source workspaces are not released to NuGet/npm.

## P1 — SQL Server implementation checklist

- [x] Pin SQL Server 2022 Developer container CI, `Microsoft.Data.SqlClient` 6.1.8 and `mssql` 12.x. Azure SQL-specific verification is still planned.
- [x] Implement `sp_getapplock` with transaction ownership + bounded wait and a unique scoped PK; avoid `HOLDLOCK` range conflicts for other keys.
- [x] Only same-key app-lock timeout maps to `InProgress`; cancellation, deadlock, connection loss and unexpected lock errors fail closed. Azure SQL and failover classifications still need verification.
- [x] Add T-SQL migration with owner fencing, UTC times, replay bytes, safe-header JSON and a unique scoped key.
- [x] Implement `IIdempotencyStore` / `IdempotencyStore` with owner fencing, uncertain state and bounded cleanup.
- [x] Implement `ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction>` and the TypeScript `mssql.Transaction` executor.
- [x] Run real SQL Server 2022 Standard/Transactional tests with 100 same-key contenders and a .NET server-session-kill **before COMMIT** test.
- [x] Run complete NestJS Express SQL Server HTTP server SIGKILL before and after COMMIT, then retry the same key through a surviving host.
- [ ] Verify equivalent ASP.NET Core HTTP server-process SIGKILL and Azure SQL failover configurations.
- [x] Add SQL Server EF Core 10 and explicit ASP.NET Core/NestJS Express provider-specific HTTP adapters in P2. See [SQL Server EF Core + HTTP](SQLSERVER_EFCORE_HTTP_V1.md).
- [x] Add a native SQL Server migration and [SQL Server Core v1](../spec/SQLSERVER_STORE_V1.md) setup, permissions, retention, locking and recovery guide; final CI evidence is tracked in `docs/VERIFICATION.md`.

## P3 — SQLite implementation checklist

- [ ] Decide supported topology, local-file ownership, WAL/synchronous policy and Node driver lifecycle.
- [ ] Implement a file-backed migration with UNIQUE scoped hash and complete response bytes.
- [ ] Choose a safe `BEGIN IMMEDIATE` strategy for the provided transaction callback and bound busy timeout.
- [ ] Define `SQLITE_BUSY`: do not falsely report unrelated writers as same-key `InProgress`.
- [ ] Test separate processes/connections and file I/O errors in a temporary **on-disk** database; never treat `:memory:` test results as proof of durability.
- [ ] Test 100 concurrent requests (expect serialized writers and documented throughput limitations).
- [ ] Verify SIGKILL before/after COMMIT, same-key retry, file durability and operational backup/cleanup.
- [ ] Only then add EF Core SQLite and NestJS adapters, with topology restrictions prominently stated.

## Decisions to resolve before P2 code

1. Which SQL Server versions and hosting models are officially supported? Start with 2022+/Azure SQL CI, and expand only after testing.
2. Do we want a dedicated temporary storage-busy exception in core, or a provider-neutral categorized storage error? Until then, expose transport-level 503 rather than inventing a `409 InProgress` for unknown database writer contention.
3. How should an app reuse the provided transaction across its repositories/ORM? Start with explicit, provider-typed callback/session; never rely on ambient connection discovery.
4. Should rollback/commit ambiguity use a documented HTTP error code and retry policy shared with Standard mode? Preserve current fail-closed behavior until tested.
5. What are the retention and cleanup SLOs for each engine, and who runs scheduled cleanup?

These are engineering decisions to resolve alongside SQL Server EF Core/HTTP integration; they do not block the tested SQL Server database-core adapters.

## Definition of done

Every provider needs a **real database** with schema migration, atomic claim correctness, explicit same-transaction business writes, cross-language result parity, contention handling, failure injection and observability. Production-grade support also needs durability and failover assumptions, authenticated HTTP tests, safe retention and deployment notes.

See [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md) and the [Verification Evidence](VERIFICATION.md).
