# Relational database provider architecture v1

**Status (2026-11-24):** Contract and provider-expansion design. PostgreSQL implements the new provider-neutral transactional interface, with real PostgreSQL tests. **SQL Server 2022 Standard and Transactional core adapters now exist and have real SQL Server CI coverage in both languages.** SQL Server EF Core 10 and ASP.NET Core/NestJS Express transactional HTTP integrations are implemented with SQL Server 2022 CI. **SQLite file-backed Standard/Transactional core is now implemented and real-database tested in .NET and Node 24.** SQLite EF Core/HTTP, MySQL and MariaDB remain future work. Azure SQL and cross-provider failover guarantees are not verified.

## Design objective

Keep the public idempotency **identity, request fingerprint, outcome, response representation and failure semantics** consistent across databases. Keep **transaction ownership, connection/session types, SQL dialect, lock acquisition, migration scripts, cleanup and durability policy** specific to each database.

This is not a portable SQL query builder. A single "SQL for every database" claim would be unsafe around atomic key claims and failure recovery.

### Layer boundaries

~~~text
Application: authenticated caller, operation ID, Idempotency-Key
      |
Core: canonical scoped SHA-256, request fingerprint, StoredHttpResponse
      |
HTTP integration: validate request, authorize on every replay, map outcomes
      |
Provider-neutral contracts:
  Standard: IIdempotencyStore / IdempotencyStore
  Transactional: ITransactionalIdempotencyExecutor<TConnection,TTransaction>
                 / TransactionalIdempotencyExecutor<TSession>
      |
Provider implementation:
  PostgreSQL (implemented) | SQL Server (core + EF Core + HTTP implemented) | SQLite (core implemented)
      |
Provider-owned connection, transaction, schema, locking and cleanup
~~~

A provider may implement **Standard mode only**, **Transactional mode only**, or both initially. Claiming both requires both integration suites. A Standard-mode store alone cannot make independently committed domain writes atomic.

## New core contracts

### .NET 10

Source: [`ITransactionalIdempotencyExecutor.cs`](../dotnet/src/IdempotencyKit.Core/ITransactionalIdempotencyExecutor.cs)

~~~csharp
public interface ITransactionalIdempotencyExecutor<TConnection, TTransaction>
    where TConnection : DbConnection
    where TTransaction : DbTransaction
{
    Task<TransactionalExecutionResult> ExecuteAsync(
        string scopeKeyHash, string requestFingerprint,
        Func<TConnection, TTransaction, CancellationToken,
            Task<StoredHttpResponse>> operation,
        TimeSpan retention, CancellationToken cancellationToken = default);
}
~~~

The callback receives **the provider's actual connection and transaction**. The executor owns BEGIN/COMMIT/ROLLBACK. The result type is independent of Npgsql: `TransactionalExecutionResult` / `TransactionalExecutionStatus`. The existing PostgreSQL concrete method still returns `TransactionalIdempotencyResult` for backward source compatibility; it implements the new interface explicitly and maps the outcomes. The caller can choose the common interface or keep using the typed PostgreSQL method.

Why generic ADO.NET types instead of plain `DbConnection` alone? Consumers such as an EF Core provider adapter need the **specific** connection/transaction type. Type erasure and accidental second connections are not a valid atomicity guarantee. There are no implicit ambient `TransactionScope` promises.

### TypeScript (Node.js / NestJS)

Source: [`transactional-executor.ts`](../typescript/packages/core/src/transactional-executor.ts)

~~~typescript
export interface TransactionalIdempotencyExecutor<TSession> {
  execute(
    scopeKeyHash: string,
    requestFingerprint: string,
    operation: (session: TSession) => Promise<StoredHttpResponse>,
    retentionMs: number,
  ): Promise<TransactionalIdempotencyResult>;
}
~~~

`TSession` is supplied by the database adapter: PostgreSQL uses `pg.PoolClient`. SQL Server now uses `mssql.Transaction` and SQLite uses typed SqliteConnection/SqliteTransaction in .NET and a restricted SqliteTransactionSession using Node 24 built-in DatabaseSync, with process-local file queuing. Business callbacks must not use independent connections. Provider-specific SQL syntax belongs in the business operation.

The PostgreSQL adapter implements the interface. Its original exported result/status types remain available through type reexports from the core, rather than newly duplicated definitions.

### Observable result contract

Cross-language fixture: [`transactional-provider-v1.json`](fixtures/transactional-provider-v1.json). Both language suites verify the mapping.

| .NET | TypeScript | Contains replay response? | Runs new business callback? |
| --- | --- | --- | --- |
| `Executed` | `executed` | Yes | Exactly once for this transaction attempt |
| `Replayed` | `replayed` | Yes | No |
| `InProgress` | `in_progress` | No | No |
| `FingerprintMismatch` | `fingerprint_mismatch` | No | No |
| `Uncertain` | `uncertain` | No | No |

These are **observed outcomes of the idempotency layer**, not proof of universal exactly-once external effects. Database connection failures, ambiguous COMMIT acknowledgements and unclassified provider lock errors **throw**, rather than masquerading as successful completion.

`InProgress` is reserved for a known competing operation/claim. A whole-database SQLite writer lock could be caused by an **unrelated** key; without evidence of a matching claim, it must not be reported as `InProgress`. Map unclassified writer contention or storage outages to a fail-closed storage error (typically HTTP 503) and document retry guidance.

The caller performs authentication and **fresh authorization before replays**, even when the same scoped identity is presented.

## Standard-mode provider contract

Existing shared interfaces: `IIdempotencyStore` (.NET) and `IdempotencyStore` (TypeScript). Each provider must implement:

- `TryClaim` / `tryClaim` with a **durable unique scoped key** and a race-safe claim; distinguish new, active, completed, mismatched and uncertain.
- `TryComplete` / `tryComplete` with **owner-token + fingerprint fencing**, a valid lease, bounded JSON response and safe header allowlist.
- `TryExtendLease` / `tryExtendLease` that never revives an expired claim.
- `TryMarkUncertain` / `tryMarkUncertain` that prevents blind reexecution after ambiguous business effects.
- `PurgeExpiredCompleted` / `purgeExpiredCompleted` that **never** automatically deletes processing/uncertain rows.

Do **not** add automatic uncertain-record reclaims just to make SQLite or SQL Server seem easy.

## Transactional provider invariants

1. **Single physical transactional unit:** reserve the scoped key, execute all business SQL via the supplied session, validate the response, persist it and COMMIT inside one database transaction.
2. **Unique key plus concurrency control:** two contenders for a scoped key cannot run two committed business callbacks. A lock timeout may report `InProgress` only where the operation is known to be the same key; other storage contention fails closed.
3. **No early success:** `Executed` is returned only after the provider acknowledges COMMIT. If the response is lost after commit, same-key retry must return `Replayed`.
4. **Safe rollback:** callback throws or validation fails before COMMIT => both business SQL and idempotency reservation roll back. Do not mark a rolled-back attempt completed.
5. **Ambiguous COMMIT:** never blindly retry the callback inside the executor. Clients retry with the **same scoped key** against the authoritative primary and evaluate its stored outcome.
6. **Existing Standard-mode records:** an active/uncertain matching record must not be stolen by Transactional mode; a completed record must be replayable under the same fingerprint.
7. **Response discipline:** valid UTF-8 JSON 2xx up to 256 KiB and only `Location` / `ETag` / `Content-Language` replay headers; no cookies, auth headers or arbitrary binary streams.
8. **Retention:** completed records expire only by explicit cleanup; a previously deleted key can be treated as new. Domain uniqueness constraints remain necessary.
9. **Durability:** database WAL/commit/synchronous replication configuration and backup/failover policy are part of the guarantee. In-memory or ephemeral stores are not interchangeable with durable replicated databases.
10. **External effects excluded:** HTTP calls, emails, payments, queues or writes to a second database do not participate in this transaction automatically.

## Provider capability model (planned, not runtime-verified)

| Capability | PostgreSQL | SQL Server / Azure SQL | SQLite |
| --- | --- | --- | --- |
| Shared app deployment | Server database, verified in CI | Server database; SQL Server 2022 Docker CI verified | **Local file / one host** by default; not network WAL |
| Atomic transactional callback | Implemented via Npgsql/pg | **Implemented** via SqlConnection/SqlTransaction and mssql.Transaction | **Implemented** SqliteConnection/SqliteTransaction and guarded Node 24 session |
| Claim serialization | Unique index, uncommitted INSERT | **Implemented** unique PK plus transaction-owned per-key sp_getapplock | `BEGIN IMMEDIATE` single writer, unique index |
| Standard-mode leases | Implemented | **Implemented**, owner-fenced and fail-closed uncertain | Planned local-file semantics |
| Queueing/timeout | PostgreSQL `lock_timeout` | **Implemented** bounded sp_getapplock same-key timeout | `busy_timeout` / `SQLITE_BUSY`; distinguish unrelated writer |
| JSON replay storage | `bytea` + `jsonb` | `varbinary(max)` + `nvarchar(max)` | `BLOB` + `TEXT` |
| EF Core | Npgsql tested | **EF Core 10 SQL Server tested** | Microsoft.EntityFrameworkCore.Sqlite planned |
| Actual provider test matrix | PostgreSQL 17 | **SQL Server 2022 real CI**; Azure SQL still unverified | **22 actual-file tests**, multi-process contention and SIGKILL before/after COMMIT |

No feature should be checked off for a provider until its real-database suite establishes the corresponding behavior.

## SQL Server provider implementation (core + EF Core + explicit HTTP implemented)

- **Priority:** first after the core abstraction, for server-hosted .NET APIs and horizontally scaled NestJS services.
- **Tested target:** SQL Server 2022 Developer container in GitHub Actions. **Azure SQL remains unverified** (including tier-specific behavior and failover).
- **Implemented drivers:** `Microsoft.Data.SqlClient` **6.1.8 LTS** in .NET and `mssql` 12.x/Tedious in Node.js. The .NET callback receives `SqlConnection` + `SqlTransaction`; TypeScript receives `mssql.Transaction` and creates every business query via `new sql.Request(transaction)`. EF Core 10 SQL Server shared-transaction bridge is implemented and tests verify exact connection ownership.
- **Schema:** unique non-null `scope_key_hash` index; canonical fingerprint; owner UUID; explicit state; UTC timestamps; binary replay bytes; safe-header JSON text; nullable completion/lease expiry fields; versioned T-SQL migration. Do not reuse PostgreSQL `jsonb`/`bytea`/`interval` syntax.
- **Implemented claim design:** open a READ COMMITTED transaction, acquire `sys.sp_getapplock` keyed by the scoped SHA-256 key (`@LockOwner='Transaction'`, bounded `@LockTimeout`), read/insert the unique row, run domain SQL on the SAME transaction, store validated response and COMMIT. Only the same-resource `-1` timeout means `InProgress`. Canceled/deadlocked/other resource errors throw and fail closed.
- **Why no `UPDLOCK/HOLDLOCK` for absent-key reads:** under SQL Server, SERIALIZABLE key-range locks can block inserts of different scoped keys sharing an index gap. The transaction-owned per-key application lock already serializes IdempotencyKit writers of that key; a unique primary key is a second safeguard. The CI verifies 100 same-key contenders and independent-key progress. Database-specific RCSI/SNAPSHOT tuning and multi-host benchmarks remain future hardening.
- **Standard-mode implementation:** separate durable claim, owner fencing, lease handling, fail-closed uncertain state, bounded cleanup batches, and interoperability with transactional records in one database. See [SQL Server Core v1](SQLSERVER_STORE_V1.md).
- **Security:** database principals, app-lock resource namespace, permissions and locking scope must be reviewed. No SQL string interpolation of user-supplied keys into query text.

Official references: [SQL Server locking and key-range locks](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide), [`sp_getapplock`](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql), [EF Core cross-context transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).

## SQLite provider implementation (core verified; EF Core/HTTP planned)

- **Topology:** ONE host, one local SQLite database file; cooperating local processes are possible. Network WAL/shared-file multi-host/multi-region deployments are **not supported**.
- **Drivers:** Microsoft.Data.Sqlite 10.0.4 in .NET; Node 24 built-in `node:sqlite` `DatabaseSync` (no additional native npm dependency). SQLite EF Core 10 and HTTP adapters remain planned for P4.
- **Schema:** `spec/sql/sqlite/001_idempotencykit_records.sql` uses a unique binary-collated scoped hash, durable BLOB response, safe JSON headers, owner fencing and UTC epoch-millisecond timestamps.
- **Transaction ownership:** SQLite WAL + synchronous FULL; `BEGIN IMMEDIATE` reserves the single FILE writer before inspecting the idempotency hash. The .NET callback uses supplied SqliteConnection / SqliteTransaction. Node uses restricted statement session; this package's same-path operations are serialized in-process even across async callbacks.
- **Busy semantics:** an unrelated table/key may hold SQLite's only write lock. `SQLITE_BUSY` and `SQLITE_LOCKED` propagate as storage errors—not `InProgress`. Only an actual matching processing record proves `InProgress`.
- **Durability/failure:** invalid responses or thrown business callbacks roll back both domain SQL and provisional claim; committed results replay across processes. Standard-mode expired claims become `Uncertain` and are never automatically reacquired.
- **Verification:** 22 tests against real temporary files, 100 same-key contenders in each language, independent connections, unsafe response corruption, foreign-process writer lock and SIGKILL before and after COMMIT.
- **Not certified:** network mounts, multi-machine deduplication, backup/restore fault injection, full .NET process-kill lab, SQLite EF Core/HTTP APIs, encryption tooling or NuGet/npm publication. See [SQLite Store v1](../docs/SQLITE_STORE_V1.md).

Official references: [SQLite WAL](https://www.sqlite.org/wal.html), [SQLite transactions](https://www.sqlite.org/lang_transaction.html), [SQLite error/busy codes](https://www.sqlite.org/rescode.html).

## Additional providers later

MySQL and MariaDB should have separate implementation and tests for their transaction isolation, unique-index race handling, lock wait/deadlocks, replication and storage durability. Do not treat MySQL and MariaDB as one identical provider without verification. An in-memory cache/Redis adapter may eventually offer **Standard mode**, but must not claim same-database transactional atomicity for arbitrary SQL business writes.

## Acceptance suite required for EACH new provider

Run each scenario against an **actual provider instance or durable local file**, not mocked interfaces:

1. Basic new key => one 201 JSON response and one committed domain row.
2. Same scoped key/fingerprint => replay exact safe response without callback.
3. Different fingerprint => mismatch and no second domain row.
4. Different authenticated users/tenants => isolated keys.
5. >=100 concurrent callers for a single scoped key => one committed effect.
6. Concurrent different keys => no incorrect `InProgress` claim or cross-key corruption.
7. Callback failure after business SQL => **both business and idempotency writes roll back**.
8. Invalid/oversized JSON response => rollback both writes before COMMIT.
9. Failed/released connection before COMMIT => no committed effect and safe same-key retry.
10. Server process killed **after COMMIT but before HTTP response** => same-key retry replays.
11. Bounded lock timeout, deadlock and database unavailable => fail closed with correct status.
12. Existing Standard-mode processing/uncertain/completed records work safely with Transactional mode.
13. Expired owner lease, owner fencing and cleanup never automatically revive uncertain work.
14. SQL injection, unsafe header replay, scoped auth isolation and sensitive data leakage tests.
15. Storage durability and failover assumptions documented with real provider configuration.

**Release gate:** no compatibility claim until build, migration, real-provider tests, observable behavior and crash evidence are complete and recorded in `docs/VERIFICATION.md`.

## Compatibility and migration policy

- The core SHA-256 scope/fingerprint format remains **v1** for all providers. Database selection must not change client key behavior.
- New provider implementations use independent migration namespaces; they do not convert PostgreSQL rows on the fly.
- Switching from PostgreSQL to SQL Server/SQLite may lose existing idempotency history. A no-downtime migration requires an explicit **single-authoritative-store cutover** strategy or a validated migration tool; never send the same key to two disconnected active stores and claim cross-store deduplication.
- Existing .NET concrete `PostgresTransactionalExecutor.ExecuteAsync` and TypeScript `PostgresTransactionalExecutor.execute` remain unchanged in this milestone.
- Refactor shared validation/orchestration only after the provider contract is tested; do not change existing Standard-mode semantics incidentally.

**Current implementation milestone:** PostgreSQL and SQL Server Standard/Transactional **core** providers implement the shared contracts with real database CI; SQL Server EF Core/HTTP are implemented and verified against SQL Server 2022; SQLite core is now verified in .NET and Node 24; SQLite EF Core/HTTP and other providers remain future milestones. See [SQL Server Core v1](SQLSERVER_STORE_V1.md).
