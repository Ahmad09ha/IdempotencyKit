# Database provider rollout plan

**Planning baseline:** PostgreSQL Standard + Transactional mode, EF Core Npgsql, ASP.NET Core and NestJS Express HTTP integration are already implemented and tested. Shared provider-neutral transactional interfaces are now being added. **SQL Server Standard and Transactional core adapters are now implemented and tested against SQL Server 2022; SQL Server EF Core 10, ASP.NET Core and NestJS Express Transactional HTTP are also implemented and tested. SQLite Standard/Transactional database-core, EF Core 10, authenticated ASP.NET Core and NestJS Express HTTP adapters are implemented and real on-disk tested in .NET and Node 24.**

## Recommended sequence

| Milestone | Scope | Exit criteria | Status |
| --- | --- | --- | --- |
| P0 — contracts | Generic .NET + TypeScript transactional interfaces, PostgreSQL compatibility, cross-language result fixture, architecture | Both existing PostgreSQL CI suites pass unchanged; new interface tests pass | Implemented |
| P1 — SQL Server core | Durable Standard-mode claim/store + explicit SQL Server transaction executor (.NET + TypeScript) | Real SQL Server 2022 CI, 100 concurrent same-key, mismatch, replay, rollback and lock-timeout tests | **Implemented; CI verified** |
| P2 — SQL Server HTTP/EF | Explicit ASP.NET Core endpoint mapping, EF Core SQL Server integration, NestJS Express service | Real authenticated HTTP request/replay, SQL Server EF shared transaction, Node HTTP process SIGKILL before/after COMMIT | **Implemented; CI verified** |
| P3 — SQLite core | Local file-backed Standard + Transactional executors in .NET/Node | **24 on-disk tests**: WAL/FULL, busy, multi-connection replay, 100 contenders, rollback and SIGKILL | **Implemented; CI verified** |
| P4 — SQLite HTTP/EF | EF Core SQLite and explicit ASP.NET Core / NestJS Express adapter, guarded single-writer callbacks | Exact HTTP replay across hosts, 16 same-key callers, unrelated writer 503, Node HTTP SIGKILL before/after COMMIT | **Implemented; CI verified** |
| P5 — more engines | MySQL/MariaDB research, optional Redis Standard mode | Separate database-specific guarantees and verified CI evidence | Research documented; implementation deferred |

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
IdempotencyKit.Sqlite                           # implemented core
IdempotencyKit.EntityFrameworkCore.Sqlite       # implemented
IdempotencyKit.AspNetCore.Sqlite                 # implemented
~~~

TypeScript:

~~~text
@idempotencykit/core                  # shared hashes + generic contracts
@idempotencykit/postgres              # implemented
@idempotencykit/nestjs                # Standard-mode core
@idempotencykit/nestjs-postgres       # implemented
@idempotencykit/sqlserver             # implemented core
@idempotencykit/nestjs-sqlserver      # implemented (Express)
@idempotencykit/sqlite                # implemented core (Node 24 built-in node:sqlite)
@idempotencykit/nestjs-sqlite         # implemented (NestJS 12 Express, Node 24)
~~~

**PostgreSQL, SQL Server, and SQLite database-core packages exist as unpublished source packages.** SQLite EF Core/HTTP source integrations exist and are tested.  The existing source workspaces are not released to NuGet/npm.

## P1 — SQL Server implementation checklist

- [x] Pin SQL Server 2022 Developer container CI, `Microsoft.Data.SqlClient` 6.1.8 and `mssql` 12.x. Azure SQL-specific verification is still planned.
- [x] Implement `sp_getapplock` with transaction ownership + bounded wait and a unique scoped PK; avoid `HOLDLOCK` range conflicts for other keys.
- [x] Only same-key app-lock timeout maps to `InProgress`; cancellation, deadlock, connection loss and unexpected lock errors fail closed. Azure SQL and failover classifications still need verification.
- [x] Add T-SQL migration with owner fencing, UTC times, replay bytes, safe-header JSON and a unique scoped key.
- [x] Implement `IIdempotencyStore` / `IdempotencyStore` with owner fencing, uncertain state and bounded cleanup.
- [x] Implement `ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction>` and the TypeScript `mssql.Transaction` executor.
- [x] Run real SQL Server 2022 Standard/Transactional tests with 100 same-key contenders and a .NET server-session-kill **before COMMIT** test.
- [x] Run complete NestJS Express SQL Server HTTP server SIGKILL before and after COMMIT, then retry the same key through a surviving host.
- [x] Verify equivalent ASP.NET Core HTTP server-process SIGKILL, and cross-runtime HTTP replay/contestation against one SQL Server 2022 instance.
- [ ] Verify Azure SQL failover configurations.
- [x] Add SQL Server EF Core 10 and explicit ASP.NET Core/NestJS Express provider-specific HTTP adapters in P2. See [SQL Server EF Core + HTTP](SQLSERVER_EFCORE_HTTP_V1.md).
- [x] Add a native SQL Server migration and [SQL Server Core v1](../spec/SQLSERVER_STORE_V1.md) setup, permissions, retention, locking and recovery guide; final CI evidence is tracked in `docs/VERIFICATION.md`.

## P3 — SQLite implementation checklist

- [x] Support one host and a LOCAL database file; WAL + synchronous FULL; Node 24 built-in SQLite; no network filesystem claims.
- [x] Implement unique scoped key schema, BLOB response, safe replay headers, owner fencing, UTC epoch-ms leases/retention.
- [x] Implement .NET SqliteConnection/SqliteTransaction and Node restricted statement session with BEGIN IMMEDIATE before callback.
- [x] Serialize this Node package's operations in-process by absolute file path; other writers still coordinate with SQLite file locks.
- [x] Propagate SQLITE_BUSY/SQLITE_LOCKED as STORAGE ERRORS, never misclassify unrelated writers as same-key InProgress.
- [x] Test independent connections/processes, owner fencing, 100 concurrent same-key calls, mismatch, rollback, cleanup and unsafe replay handling on an actual temp SQLite file.
- [x] Kill separate Node process before/after COMMIT, verify file recovery and same-key replay without a second insert.
- [x] P4: SQLite EF Core 10 and authenticated ASP.NET Core/NestJS Express integrations with new file-backed real HTTP and EF tests.
- [x] Full .NET SQLite API-process SIGKILL, coordinated mixed-runtime local file, online backup/restore, pinned checkpoint and simulated SQLite capacity limit.
- [x] Verify actual Linux filesystem permission denial and safe same-key recovery in both SQLite HTTP adapters.
- [ ] Host disk exhaustion and production deployment verification remain open.

See [SQLite Store v1](SQLITE_STORE_V1.md) and [Verification Evidence](VERIFICATION.md). P5 research and its required acceptance evidence are tracked in [Next Providers](NEXT_PROVIDERS.md).

## P4 — SQLite EF Core and explicit HTTP checklist

- [x] EF Core 10 bridge joins exact SQLite connection + transaction and fails before callback if factory uses another connection.
- [x] ASP.NET Core authenticated transaction-aware JSON endpoints and fresh authorization before replay.
- [x] NestJS 12 Express rawBody transaction-aware service/module using guarded SQLite statement session.
- [x] Real-file EF Core and two-host HTTP replay, mismatch, 16-way contention, owner/lease/uncertain and error-after-SQL rollback.
- [x] Foreign writer `SQLITE_BUSY` results in HTTP 503, not 409 proven same-key conflict.
- [x] Node full HTTP process SIGKILL before COMMIT and after COMMIT/before reply, each followed by safe same-key retry.
- [x] Full .NET HTTP process SIGKILL, mixed .NET/Node access to one local file, online backup/checkpoint validation and SQLITE_FULL page-cap rollback.
- [x] Verify Linux permission faults on isolated local files; read-only replay may remain available, fully inaccessible storage must fail closed.
- [ ] True host disk-full, production filesystem/ACL and failover environments remain unverified.
- [ ] NuGet/npm publishing and production audit.

See [SQLite EF Core + HTTP v1](SQLITE_EFCORE_HTTP_V1.md).

## Decisions for release hardening

1. Which SQL Server versions and hosting models are officially supported? Start with 2022+/Azure SQL CI, and expand only after testing.
2. Do we want a dedicated temporary storage-busy exception in core, or a provider-neutral categorized storage error? Until then, expose transport-level 503 rather than inventing a `409 InProgress` for unknown database writer contention.
3. How should an app reuse the provided transaction across its repositories/ORM? Start with explicit, provider-typed callback/session; never rely on ambient connection discovery.
4. Should rollback/commit ambiguity use a documented HTTP error code and retry policy shared with Standard mode? Preserve current fail-closed behavior until tested.
5. What are the retention and cleanup SLOs for each engine, and who runs scheduled cleanup?

These are future hardening decisions; verified core behavior does not imply production certification or released NuGet/npm packages.

## Definition of done

Every provider needs a **real database** with schema migration, atomic claim correctness, explicit same-transaction business writes, cross-language result parity, contention handling, failure injection and observability. Production-grade support also needs durability and failover assumptions, authenticated HTTP tests, safe retention and deployment notes.

See [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md) and the [Verification Evidence](VERIFICATION.md).
