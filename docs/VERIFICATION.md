# Verification Evidence — IdempotencyKit

This file records **observed test results**, separately from planned guarantees. A test passing on a simulated failure or single CI host is **not** equivalent to production failure certification.




## 2026-11-24 — Preview readiness and additional SQLite fault coverage

Local verification of the release-candidate changes, continuing from main `f2eae3c`. GitHub Actions should independently certify the exact PR head before merge.

| Run | Result |
| --- | --- |
| NuGet preview | All **11 .nupkg** files inspected; isolated .NET consumer restored and ran a real SQLite claim. |
| npm preview | All **eight tarballs** packed, installed outside workspace, type-checked and loaded; isolated consumer ran a real SQLite claim. |
| Real PostgreSQL 17.12 (.NET solution) | **40 passed**, no failures/skips, including build and HTTP/provider regressions. |
| Node workspace with PostgreSQL 17.12 | **66 passed**, **19 SQL Server-only tests skipped** in the local environment, no failures. |
| SQLite .NET suite prior to capacity add-on | **27 passed**, including new standalone Kestrel crash tests and telemetry. The separately run page-cap test also passed. |
| SQLite mixed runtime and online maintenance | **5 passed**: three shared-file C#/Node tests and two backup/checkpoint tests. |
| Both runnable Orders demos | Real HTTP original `201`, byte-identical retry and `Location`, changed payload `422`, unauthenticated retry `401`, one durable order; replay also passed after a host restart using the same file. |

- **ASP.NET Core / Kestrel SQLite process kills:** killing a separate process before COMMIT leaves neither the provisional record nor the domain write. Killing it after COMMIT but before headers leaves a durable replay without another write. Both use an actual local file and a separate retrying host.
- **Mixed runtimes:** .NET and Node alternate ownership/replay against the same SQLite file, verify matching fingerprints/bytes, and coordinate through SQLite itself. A foreign writer remains a storage failure, not proven same-key conflict.
- **SQLite maintenance and capacity:** verify a coherent online backup taken during an active writer, restore and replay; force a WAL truncate checkpoint to wait for a pinned reader; impose an engine page cap to produce `SQLITE_FULL` and prove rollback before same-key retry. This is **not** actual host-disk exhaustion or a filesystem permission/failover test.
- **Telemetry**: new HTTP observer/meter verifies bounded outcome identifiers and does not add the caller, key or body to observations. See [Operations](OPERATIONS.md).
- **Dependency servicing**: updated the .NET SQLite libraries to 10.0.15; Linux x64 resolved `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 (SQLite runtime 3.53.3). NuGet's transitive audit no longer reported [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) for the SQLite suite closure.

**Still open:** current-head SQL Server real database regression is gated on its dedicated CI or an available SQL Server instance. Live ASP.NET process-kill coverage for PostgreSQL and SQL Server, Azure SQL failover, full multi-replica pressure, production permission and physical disk fault tests, external authentication and performance/security audits are not certified. Fastify, Redis, MySQL/MariaDB, automatic uncertain-state reconciliation and actual registry publication are not claimed.

## 2026-11-24 — SQLite EF Core 10 + authenticated transactional HTTP (P4)

**Verified source commit:** `77cffa9fd2eb1323233e16111a187a45210e167c` on `feature/sqlite-efcore-http-v1`.

- [SQLite real-file P4 CI run 45380771946](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45380771946): **24 .NET + 24 Node = 48 passed, 0 failed, 0 skipped**. Includes all 24 previously verified P3 database-core tests.
- [PostgreSQL regression run 45380771971](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45380771971): **40 .NET passed** and **62 Node passed** (38 existing PostgreSQL/shared + 24 SQLite); the PostgreSQL-only Node workflow intentionally skips 19 SQL Server-specific tests, all executed separately.
- [SQL Server 2022 regression run 45380772112](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45380772112): **22 .NET + 19 Node = 41 passed**, 0 failed/skipped.

| SQLite real-file suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| .NET 10 — core + EF Core 10 + ASP.NET Core TestServer | **24** | 0 | 0 |
| Node 24 — core + NestJS 12 Express + live HTTP SIGKILL | **24** | 0 | 0 |
| **SQLite P4 total** | **48** | **0** | **0** |

### P4 scenarios actually verified

- **EF Core SQLite:** fresh `DbContext` bound to the exact `SqliteConnection` and enlisted via `UseTransactionAsync` in the **same SqliteTransaction** used by the idempotency record. A different connection to the **same file** is rejected before business code.
- EF Core generated IDs/`SaveChangesAsync` and persisted JSON result commit as one transaction; a second context replays without invoking handler.
- EF Core handler throws **after SaveChangesAsync**: domain row and provisional claim both roll back, allowing a same-key retry to execute once. Remaining tracked changes save before COMMIT.
- **16 concurrent same-key EF Core calls** create exactly one committed SQLite order; any `SQLITE_BUSY` exception is recorded as a separate storage failure, not a fabricated idempotency result.
- **ASP.NET Core:** authenticated SQLite-specific `MapSqliteTransactionalPost` in two independently constructed TestServer hosts using the **same local database file**; status/body/`Location` replay identically without second handler execution.
- HTTP 401/403 authentication/permission and resource-level authorization **before replay**, 400 key/malformed JSON, 422 changed request, 409 with known committed processing row (`Retry-After`), and 409 uncertain state without business reexecution.
- ASP.NET Core handler exception after a domain INSERT rolls back both writes and permits same-key retry; **16 concurrent HTTP calls** create one order.
- **SQLite-specific file-wide contention test:** an unrelated SQL writer holds a transaction on another row; the ASP.NET Core transactional route returns **503** (storage unavailable), **not** spurious 409 or another business callback. After release, same-key retry commits once.
- **NestJS 12 Express:** guarded route using `rawBody: true` and explicit `SqliteTransactionalHttpService.handleExpress`; byte-for-byte saved replay and safe headers, canonical JSON property reordering, trusted principal/tenant separation, fresh authorization, 400/403/409/422/503 mappings, 16 HTTP retries and business callback rollback.
- **Node HTTP unrelated-process writer test:** another actual SQLite process holds a file-wide writer lock; request fails 503, not 409, and retry after SIGKILL completes once.
- **Live NestJS Express API-process SIGKILL BEFORE COMMIT:** separate HTTP server is terminated while its callback holds an uncommitted domain INSERT and idempotency claim; both roll back. Same-key retry through the surviving HTTP host writes once.
- **Live NestJS Express API-process SIGKILL AFTER COMMIT but BEFORE HTTP reply:** separate server is killed after SQLite acknowledges COMMIT with response intentionally withheld; retry through the surviving host replays the saved JSON **without another domain INSERT or callback**.
- All existing P3 owner fencing, 100-way database concurrency, schema parity, uncertain lease, corrupt replay and process-crash tests remain green in this suite.

### Explicit boundaries (not certified)

- SQLite **local on-disk file and single-host topology only**; WAL and its sidecar files must not be shared across machines via NFS/SMB. This is not a distributed idempotency service.
- No standalone **.NET API-process** SIGKILL test at the HTTP layer; .NET TestServer uses the real SQLite database but lives inside the test process. The actual before/after COMMIT HTTP process-kill lab here is **Node/NestJS** only.
- SQLite .NET and Node suites each use their **own temporary file**; simultaneous mixed-language traffic from both runtimes against **one shared file** is a future interoperability stress test.
- No runtime Fastify adapter; no automatically transactional `[Idempotent]` or `@Idempotent()` decorator—explicit transactional routes must not be wrapped in Standard-mode idempotency middleware.
- No production online backup/restore validation, checkpoint/disk-full or filesystem permission fault injection, encryption/security/performance certification, NuGet/npm package publication or auto reconciliation.
- Real JWT/cookie validation and tenant authorization belong to the consuming app; the CI `X-Test-User` guard is **test-only**.
- No single SQLite transaction can roll back emails, HTTP calls, payments, messages, another file or a remote database; retry ambiguous COMMIT outcomes with the **same key against the same authoritative file**.

**Conclusion:** SQLite P4 delivers verified **file-backed core + EF Core + explicit ASP.NET Core/NestJS transactional HTTP** in both ecosystems. The local single-writer support and release-hardening boundaries remain explicit.

See [SQLite EF Core + HTTP v1](SQLITE_EFCORE_HTTP_V1.md), [SQLite Store v1](SQLITE_STORE_V1.md) and [Database Provider Rollout](DATABASE_PROVIDER_ROADMAP.md).

## 2026-11-24 — SQLite on-disk Standard + Transactional core (P3)

**Verified implementation commit:** `f90d44f4dac2a482cc0ddfd4c0628b06d8f629c2` on `feature/sqlite-core-v1`.

- [SQLite File-backed CI run 45365414949](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45365414949): **10 .NET + 14 Node = 24 passing real on-disk SQLite tests**, 0 failed, 0 skipped.
- [PostgreSQL regression CI run 45365414955](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45365414955): .NET **40 passed**, Node **52 passed** (existing 38 PostgreSQL/core + the 14 new file-backed SQLite tests), 0 failed. SQL Server-only Node tests are intentionally skipped in this PostgreSQL-only job (19 skips), but all run in SQL Server CI.
- [SQL Server 2022 regression run 45365414962](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45365414962): **22 .NET and 19 Node passed**, 0 failed, 0 skipped.

| SQLite real-file tests | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| .NET 10 / Microsoft.Data.Sqlite 10.0.4 | **10** | 0 | 0 |
| Node 24 / built-in node:sqlite | **14** | 0 | 0 |
| **SQLite total** | **24** | **0** | **0** |

### SQLite behavior verified

- SQLite migration and initialization against **actual isolated temporary filesystem files**, with `journal_mode=WAL` and `synchronous=FULL`, not `:memory:` databases.
- Standard-mode new claims, owner-token fencing, rejected false owners, lease extension, 2xx JSON completion and persisted byte-for-byte replay from a new physical connection/store.
- Same scoped key with changed fingerprint => mismatch and no second business callback.
- Expired Standard-mode processing claims become `Uncertain`, never automatically reacquired.
- Corrupted processing records with missing/null lease timestamps fail closed as `Uncertain` in both .NET and Node; no domain callback is allowed.
- SQLite Transactional mode uses `BEGIN IMMEDIATE` and the **provided SqliteConnection/SqliteTransaction or restricted Node statement session** for both business row and replay response before a single COMMIT.
- Handler failure or unsafe response headers after the business INSERT => **rollback both** the domain row and provisional claim, allowing a genuine same-key retry of a rolled-back transaction.
- **100 concurrent same-key** attempts in both ecosystems create **one** committed domain row/result. Node serializes its own local same-file operations and .NET records any thrown SQLITE_BUSY storage errors separately from idempotency outcomes.
- A separate Node process holds a transaction writing an **unrelated row/key**: the second connection gets `SQLITE_BUSY` as a storage error, **never** a spurious `InProgress`. .NET separately verifies this file-wide writer contention.
- Bounded retention cleanup deletes only expired **completed** results, preserving uncertain rows.
- An already-completed DB record tampered with a syntactically valid but unsafe `Set-Cookie` header fails closed at replay time.
- Node transactional session rejects manual COMMIT and disguised leading SQL comment/semicolon variants, preventing business callback from bypassing validation and the executor-owned atomic COMMIT.
- The Node inline SQLite migration text is checked for **exact normalized DDL parity** with the published `spec/sql/sqlite/001_idempotencykit_records.sql` migration. The SQLite source package now explicitly requires Node 24+.
- **Node SIGKILL before COMMIT:** independent worker process is killed while business row and claim are uncommitted; both disappear, then a same-key retry commits once.
- **Node SIGKILL after COMMIT but before caller acknowledgement:** independent worker is killed after COMMIT; same-key retry from another executor replays saved bytes without another SQL INSERT.

### Deliberate limits / not verified

- SQLite is supported only for **one host with a local file**; no NFS/SMB network WAL, multi-machine replicas, multi-region deduplication or shared distributed idempotency.
- SQLite **EF Core** and provider-specific ASP.NET Core/NestJS HTTP routes are not implemented in P3 (planned P4). An internal same-database callback is not automatically an HTTP API.
- Node SQLite uses synchronous `DatabaseSync` internally; a process-local queue prevents **this package's** same-file operations from blocking each other's async callbacks, but unrelated programs/processes are governed by SQLite's global file lock and may fail storage-busy. Avoid long async external work inside open transactions.
- .NET process-level SIGKILL **before/after COMMIT** is not tested yet; the process-crash suite is Node only. Complete SQLite online backup/restore, checkpoint, disk-full and file-permission fault injection are also pending.
- .NET and Node are run in **separate CI jobs** with their own files, so simultaneous mixed-language access to one SQLite file is not yet explicitly tested. The schema and v1 identity/fingerprint contracts are shared, but cross-process parity in one run should be verified before making that stronger claim.
- No published NuGet/npm packages, security/performance certification, SQLite network-failover guarantees, automatic external-effect exactly-once or arbitrary reconciliation guarantees.

**Result:** SQLite **file-backed database-core** v1 implements a conservative single-host, single-writer same-database idempotency contract with real-database crash and contention tests, without claiming full framework HTTP coverage or broader distributed durability.

See [SQLite Store v1](SQLITE_STORE_V1.md) and [Database Provider Roadmap](DATABASE_PROVIDER_ROADMAP.md).

## 2026-11-24 — SQL Server EF Core 10 + transactional HTTP integrations (P2)

**Verified source commit:** `f35d0ba14cfe410ac1f56501a432de342ff798d9` on `feature/sqlserver-efcore-http-v1`.  
**Real SQL Server 2022 CI:** [run 45360495990](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45360495990): **22 .NET and 19 TypeScript tests passed, 0 failed, 0 skipped.**  
**PostgreSQL regression CI:** [run 45360496137](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45360496137): .NET **40 passed** (9 core + 13 PostgreSQL + 5 EF Core + 13 ASP.NET Core) and TypeScript/NestJS **38 passed**. The PostgreSQL-only Node workflow discovers **19 SQL Server tests** but intentionally skips them due to no SQL Server connection; all 19 are executed separately in SQL Server CI.

| SQL Server 2022 suite | Tests | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| .NET SQL Server core + EF Core 10 + ASP.NET Core HTTP | 22 | **22** | 0 | 0 |
| Node/TypeScript SQL Server core + NestJS Express HTTP/crash | 19 | **19** | 0 | 0 |
| **SQL Server total** | **41** | **41** | **0** | **0** |

### New P2 verified behavior

- **EF Core 10 shared transaction:** a new `SqlServerEfCoreTransactionalExecutor<TContext>` creates a fresh context on the **same exact SqlConnection** and enlists the executor's existing `SqlTransaction`. Domain `SaveChangesAsync` and completed idempotency response commit as **one SQL Server transaction**.
- EF Core generated keys and saved JSON replay match, including a callback invoked only on first execution; a wrong-connection factory is rejected before business effects.
- An exception **after EF Core SaveChangesAsync** rolls back the domain row and idempotency claim together; remaining tracked entities save before the single COMMIT.
- **Twelve concurrent EF Core same-key requests** yield one committed business row.
- **ASP.NET Core `MapSqlServerTransactionalPost`:** real authenticated TestServer endpoints accept raw JSON and return stored JSON byte-for-byte across **two separate HTTP host instances** sharing the same database. They preserve safe `Location` headers, reject 422 mismatch and 400 missing/invalid key, verify principal isolation, and authorize before both execution and replay.
- ASP.NET Core business callback exception after an SQL INSERT rolls back both writes and returns 503, allowing a same-key retry; 12 simultaneous HTTP requests create one order; 409 in-progress response includes `Retry-After`.
- **NestJS 12 Express `SqlServerTransactionalHttpService.handleExpress`:** rawBody JSON scope/fingerprint, guarded caller identity and optional fresh authorization before replay, 400 missing key, 403 permission denial, 422 mismatches, 409 in-progress, exact result replay, 12 concurrent HTTP requests, user/tenant scoping and business-error rollback.
- **Live HTTP process crash before COMMIT (Node):** a separate NestJS Express server was killed with `SIGKILL` while its domain SQL and provisional claim remained uncommitted; SQL Server rolled both back and a same-key retry through the surviving HTTP host executed one domain INSERT.
- **Live HTTP process crash after COMMIT but before HTTP response (Node):** the separate server was killed after SQL Server acknowledged COMMIT while the response was deliberately withheld; a same-key retry replayed the saved JSON without another business callback or INSERT.
- All 21 previously verified SQL Server core tests remain included in the 41-test suite, including 100 same-key database concurrency, owner fencing, read-time unsafe replay rejection and backend-session kill before COMMIT.

### Scope and release boundaries

- **No equivalent full ASP.NET Core process-kill HTTP test** yet (the .NET tests use real SQL Server through TestServer, not a separately killed API process).
- SQL Server CI uses **SQL Server 2022 Developer containers**, not Azure SQL Database tier/failover, geo-replication, failback or ambiguous network-COMMIT-ack tests.
- The isolated .NET and TypeScript CI jobs each create their **own SQL Server instance**; cross-language workloads sharing the **same** server during one simultaneous test are not yet verified.
- Authentication identities injected by the CI test guard are **test-only**. Apps must configure real JWT/session validation, current resource permissions, secret management and TLS certificate validation before deployment.
- No Fastify adapter, SQL Server automatically transactional `@Idempotent()` or `[Idempotent]` annotation, full operational reconciliation, NuGet/npm registry publication or externally audited performance/security certification.
- Single SQL Server transaction guarantees **neither universal exactly-once effects nor atomic payments, emails, queues or writes to another database**. The client must retry a potentially ambiguous COMMIT using the **same scoped idempotency key** against the authoritative primary.

See [SQL Server EF Core + HTTP v1](SQLSERVER_EFCORE_HTTP_V1.md), [SQL Server Core v1](../spec/SQLSERVER_STORE_V1.md) and [Database Provider Roadmap](DATABASE_PROVIDER_ROADMAP.md).

## 2026-11-24 — SQL Server 2022 Standard and Transactional core (P1)

**Verified source commit:** `b064965e5061246dc9ad4fac8e891f82b7c766a6` on `feature/sqlserver-core-v1`.  
**SQL Server CI:** [run 45302246617](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45302246617) — both jobs passed against **actual SQL Server 2022 Developer containers**.

| Ecosystem | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| .NET 10 / Microsoft.Data.SqlClient 6.1.8 | **11** | 0 | 0 |
| Node.js 24 / mssql 12.x / Tedious | **10** | 0 | 0 |
| **Total SQL Server provider tests** | **21** | **0** | **0** |

**PostgreSQL regression CI:** [run 45302246723](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45302246723): .NET **40 passed**, TypeScript **38 passed**, no failures. Node also discovers the 10 SQL Server tests in the PostgreSQL-only CI job; it intentionally **skips those 10** because no SQL Server connection was configured there. All 10 are run in the dedicated SQL Server CI above.

### SQL Server scenarios actually verified

- SQL Server-specific DDL migration, binary-collated unique scoped key and safe response storage.
- Standard mode: fresh claim, owner-token fencing, lease extension, completion and byte-for-byte replay.
- Same key + different request fingerprint returns a mismatch without domain execution.
- Expired Standard-mode processing claims become `Uncertain` and are not reclaimed.
- Transactional mode: same `SqlConnection`/`SqlTransaction` or `mssql.Transaction` executes business SQL and stores the replay result before one COMMIT.
- Standard and Transactional modes share the same SQL Server table and key-lock resource; completed Transactional results can be replayed through Standard mode.
- Handler exception or unsafe response before COMMIT rolls back both the domain row and the provisional idempotency claim.
- **100 concurrent same-key operations in each ecosystem** create exactly one committed business effect; duplicates return safe results.
- Per-key app locks do not block separate-domain-key transaction execution. .NET no longer uses unnecessary serializable `HOLDLOCK` key-range reads on absent keys.
- Explicit bounded app-lock wait returns `InProgress` for known same-key contention; unexpected database failures are not silently mapped to a replayable success.
- Bounded cleanup deletes expired completed rows and retains uncertain records.
- **.NET injected SQL Server connection termination while business SQL was uncommitted:** neither the order nor claim survived, and a same-key retry committed once.
- **Stored response corruption:** syntactically valid but unsafe `Set-Cookie` header injected into the database is rejected at replay time by both language implementations.

### Boundaries not yet verified

- No SQL Server **live HTTP server SIGKILL after COMMIT but before reply**, nor SQL Server EF Core or ASP.NET Core/NestJS provider-specific HTTP adapters in this milestone.
- No **Azure SQL** deployment-tier, failover, geo-replication or lost-COMMIT-ack validation. Local SQL Server 2022 CI is not certification for those environments.
- No actual cross-language instance pair writing the **same database** in one test run (the .NET and Node GitHub Actions jobs each provision an independent disposable SQL Server container); both obey the same scoped hash format, schema and app-lock resource name.
- No production throughput benchmark or audited security configuration; SQL Server application roles, TLS certificates, backup, retention and failover must be configured independently.
- External payments, emails, queues and writes to independent databases are **not** made atomic by this single SQL Server transaction.
- No SQL Server NuGet/npm package publication, and no SQLite adapter yet.

**Conclusion:** this establishes the SQL Server **database-core** implementation against the shared v1 contract in real SQL Server 2022 CI. It does **not** establish framework-HTTP, EF Core or full failover/release guarantees.

See [SQL Server Core v1](../spec/SQLSERVER_STORE_V1.md), [Provider Architecture](../spec/PROVIDER_ARCHITECTURE_V1.md) and [Database Provider Roadmap](DATABASE_PROVIDER_ROADMAP.md).

## 2026-11-24 — Provider-neutral transactional contracts (P0)

**Verified source commit:** `f526406597e26e25de937fc38eccc62048d347c4` on `feature/provider-neutral-contracts-v1`.  
**GitHub Actions:** [run 45300000437](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45300000437). Both projects compiled and passed **all tests** against PostgreSQL 17.

| Ecosystem | Tests passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| .NET 10 (core, Npgsql, EF Core, ASP.NET Core) | **40** | 0 | 0 |
| Node.js / TypeScript / NestJS Express | **38** | 0 | 0 |
| **Total** | **78** | **0** | **0** |

**New evidence:** both language suites validate the same `spec/fixtures/transactional-provider-v1.json` outcome mapping, and the Npgsql executor is exercised **through its new provider-neutral typed interface** with a real committed business row, replay, and fingerprint mismatch. The TypeScript PostgreSQL class implements the generic session contract at compile time; all existing PostgreSQL transactional, Standard-mode, HTTP, EF Core, and crash tests remain green.

**Not verified:** SQL Server and SQLite **do not have executable adapters or CI services yet**. The SQL Server/SQLite architecture and rollout documents are proposals only; no provider-specific correctness claims are made. PostgreSQL remains the sole tested provider for actual durable SQL idempotency.

See [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md) and [Database Provider Rollout](DATABASE_PROVIDER_ROADMAP.md).

## 2026-11-23 — EF Core 10 + PostgreSQL Transactional HTTP integration

**Verified source commit:** `6ca75715397de09500185a288406956e20ac9b85` on feature branch `feature/efcore-transactional-http-v1`.  
**GitHub Actions:** [run 45248594225](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45248594225). Both jobs built successfully and all tests passed against PostgreSQL 17.

| Ecosystem | Test cases | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| .NET 10 (core + Npgsql + EF Core + ASP.NET Core) | 37 | **37** | 0 | 0 |
| TypeScript / NestJS Express + PostgreSQL 17 | 36 | **36** | 0 | 0 |
| **Combined** | **73** | **73** | **0** | **0** |

.NET breakdown: 7 core, 12 PostgreSQL stores/transactional callbacks, 5 real EF Core, 13 HTTP integration tests. The TypeScript total includes 9 new transaction-aware HTTP tests (7 normal HTTP scenarios and 2 full live-server crash tests).

### Verified new scenarios

- **EF Core 10:** fresh DbContext configured with the executor's exact NpgsqlConnection; UseTransactionAsync; SaveChangesAsync + replay record committed together.
- An EF Core callback that throws **after SaveChangesAsync** rolls back both the business row and idempotency row.
- EF Core factory configured with a **different connection** is rejected before business execution.
- Remaining EF tracked changes save before COMMIT; 12 concurrent same-key EF callbacks yield one committed business row.
- **ASP.NET Core:** provider-specific `MapTransactionalPost` with raw-body fingerprinting, authenticated route and optional resource permission check.
- ASP.NET Core returns the completed result across two HTTP hosts without running a second business callback, and rejects missing key/changed fingerprint.
- ASP.NET Core transactional handler error rolls back instead of producing a Standard-mode uncertain record; 12 concurrent HTTP requests run one callback.
- **NestJS Express:** explicitly called `PostgresTransactionalHttpService.handleExpress` on routes guarded before execution, with rawBody capture and exact stored JSON response emission.
- NestJS tests cover replay, mismatch, in-flight 409, missing key 400, user/tenant isolation, fresh authorization denial, 12-way concurrent HTTP retries and rollback.
- **Real HTTP server SIGKILL (Node):** a forked NestJS Express server receives an actual HTTP request and is killed while holding an uncommitted database transaction. The request fails; a retry through a surviving test host creates one order.
- **Real HTTP server SIGKILL (Node):** a forked server commits the order and replay record, then is killed before responding; a same-key retry through the surviving host replays the stored JSON with **no additional INSERT**.

### What these results do NOT establish

- An equivalent **ASP.NET Core process** SIGKILL/restart lab has not been run.
- There is no SQL Server/SQLite transactional adapter, multi-region PostgreSQL failover/replication test, or automatic transaction-aware controller decorator.
- The tests use local CI PostgreSQL 17 with ordinary primary/connection durability. They do not certify asynchronous failover guarantees.
- Authentication guards used in these specific tests are **test-only simulated identities**. Production real JWT/cookie verification, resource authorization, secrets/configuration and operational reconciliation remain the consuming application's responsibility.
- Both PostgreSQL business operations and their completed responses share the specified transaction. This does **not** guarantee exactly-once external payments, email or cross-database side effects.

For actual integration APIs and scope of support see [Transactional HTTP + EF Core v1](TRANSACTIONAL_HTTP_AND_EFCORE_V1.md).

## 2026-11-23 — PostgreSQL Transactional Mode v1

**Verified source commit:** `8b933cea92f72b15256a233884c85ac5100c16e1` on `feature/transactional-postgres-v1`.  
**GitHub Actions:** [Build and PostgreSQL Integration CI — run 45239709855](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45239709855). Both jobs completed successfully.

| Ecosystem | CI runtime | Tests passed | Failed | Skipped |
| --- | --- | ---: | ---: | ---: |
| .NET 10 / Npgsql | GitHub Actions Ubuntu + PostgreSQL 17 | **28** | 0 | 0 |
| Node.js 24 / TypeScript / NestJS 12 | GitHub Actions Ubuntu + PostgreSQL 17 | **27** | 0 | 0 |
| **Total** | | **55** | **0** | **0** |

The .NET total comprises 7 core tests, 12 PostgreSQL store/transactional tests, and 9 ASP.NET Core HTTP integration tests. The Node total comprises shared fingerprint tests, Standard-mode store and NestJS HTTP tests, **7 PostgreSQL Transactional-mode tests**, and **2 forked-worker SIGKILL tests**.

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
- **Actual process SIGKILL injection (Node):** a separately forked worker is killed after writing an uncommitted business row, before COMMIT; both rows are absent and a same-key retry commits once.
- **Actual process SIGKILL injection (Node):** a separately forked worker is killed **after** successful COMMIT but before returning a result to its parent; the retry gets a persisted replay and does not execute a second order.
- Node-specific test: while the transaction is open, a second connection cannot observe the uncommitted records, and a bounded claim-lock wait returns `InProgress`.
- Node's PostgreSQL pool handles the expected delayed disconnect event from that backend-termination test without an unhandled exception.

### Earlier Standard-mode verification retained

- Canonical SHA-256 request hashing, cross-language JSON fixture parity, scoped identities.
- PostgreSQL Standard-mode atomic claim/owner fencing, lease extension, uncertain outcomes, response storage and cleanup.
- ASP.NET Core Minimal API and MVC protected routes, auth isolation, duplicate detection and replay.
- NestJS Express guarded routes, raw-body fingerprinting, in-progress 409, mismatched 422, and completed-response replay.

### Known verification gaps (not yet asserted)

- **No complete HTTP-server-process SIGKILL lab** with HTTP clients, network disconnects and restarted API replicas. The verified forked-worker SIGKILL cases test the **transactional database boundary** rather than an HTTP host's full request pipeline.
- **No failover or asynchronous replication loss** injection; PostgreSQL primary durability settings matter.
- No two-independent-HTTP-replica stress/benchmark of Transactional mode (the 24-way concurrency tests use separate pooled connections, and the SIGKILL tests fork a non-HTTP worker process).
- No EF Core `DbContext` transaction adapter verified against Npgsql.
- No Transactional-mode HTTP attribute/decorator that automatically creates the shared business transaction.
- No external payment/email/queue exactly-once guarantees, no Fastify adapter, no released NuGet/npm packages, no production deployment/security audit.
- .NET HTTP test host currently logs framework obsolete-API warnings; these warnings do not indicate a failed build/test.

**Conclusion:** the database callback APIs have verified atomicity for supported PostgreSQL SQL operations in the tested cases. Do not upgrade this evidence into network-level or external side-effect exactly-once claims.

See [Transactional Mode v1](../spec/TRANSACTIONAL_MODE_V1.md), [Guarantees](../spec/GUARANTEES.md), and [Failure Matrix](../spec/FAILURE_MATRIX.md).
