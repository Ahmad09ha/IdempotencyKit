# P5 provider decision: MySQL, MariaDB and Redis

**Status: documentation evaluation only.** No new adapter, supported version, runtime result, compatibility promise or release dependency is established here. Start from the [failure matrix](../spec/FAILURE_MATRIX.md), preserve the [provider contract](../spec/PROVIDER_ARCHITECTURE_V1.md), and list observed results separately in [Verification Evidence](VERIFICATION.md).

## Recommendation

| Candidate | Potential scope | Decision |
| --- | --- | --- |
| MySQL 8.4 / InnoDB | Standard first; explicit same-transaction executor second | Prototype first if there is user demand. Pin and test one exact server/driver combination in each runtime. |
| MariaDB / InnoDB | Same outcome contract, separate server qualification | Choose an explicit supported version and run its own CI. Do not claim compatibility based on MySQL passing. |
| Redis | Standard coordination only, with qualified durability and an independent business guard | Defer. A Redis-only result store is not a replacement for the supported SQL transaction path. |

## InnoDB: one transaction, known failure boundaries

MySQL documents row, gap and next-key contention, as well as different duplicate-key locking on plain inserts and upserts. MariaDB documents that upsert return counts can depend on client flags. **Design implication:** let one unique scope-key constraint protect the claim; inspect the persisted fingerprint and state under a tested locking strategy. Never infer ownership from a generic affected-row count, overwrite a conflicting fingerprint, or treat a lock timeout alone as proof of same-key work. [MySQL locks by statement](https://dev.mysql.com/doc/refman/8.4/en/innodb-locks-set.html) · [MariaDB upsert behavior](https://mariadb.com/docs/server/reference/sql-statements/data-manipulation/inserting-loading-data/insert-on-duplicate-key-update).

A deadlock can roll back the whole transaction; an InnoDB lock timeout can roll back just the statement, depending on server settings. Both servers also have statements, including DDL, that can implicitly commit the active transaction. **Design implication:** own the full transaction lifecycle, fail closed on uncertain driver or storage errors, and explicitly abandon/roll back the unit of work on failure. No implicit retry of a business callback after an ambiguous commit. Apply schema changes outside the callback and require transactional tables plus the supplied connection/transaction for all covered writes. [MySQL error handling](https://dev.mysql.com/doc/refman/8.4/en/innodb-error-handling.html) · [MariaDB InnoDB timeout settings](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-system-variables) · [MySQL implicit commits](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html) · [MariaDB implicit commits](https://mariadb.com/docs/server/reference/sql-statements/transactions/sql-statements-that-cause-an-implicit-commit).

**Implementation gates, for each engine independently:**

1. Versioned native migration: binary/exact key equality, bounded replay bytes and headers, UTC lease/retention and indexes. Version-pin the driver, isolation level, lock timeouts and server durability profile. Verify migration and grants on a real database in both .NET and Node.
2. Two processes and 100 simultaneous same-key attempts: one active Standard owner and one Transactional commit, matching requests replay, changed payload returns 422 without invoking the handler. Exercise unrelated writers, lock timeout, deadlock, cancellation and connection loss; return `InProgress` only on evidence of same-key contention. Compare independent-key progress and use bounded waits.
3. Same database and connection: prove callback error and process death before COMMIT erase both effects, and death after COMMIT but before reply replays the stored response. Test loss of commit acknowledgement using a fresh connection and the same scoped key. Start single primary; do not claim promotion/failover safety without its own tests.
4. Run shared fixtures and authenticated HTTP parity; test stale owners, permission changes before replay and bounded completed-only cleanup. Add EF Core or a framework integration only after proving exact transaction enlistment and real HTTP behavior on the chosen provider.

## Redis: atomic store updates are not durable business atomicity

A small server-side script could coordinate conditional claims/completions inside one Redis server. It cannot join the application's SQL transaction. Eviction can remove an in-flight or completed key; `noeviction` avoids eviction at the cost of rejecting writes when capacity is exhausted. AOF persistence depends on the fsync policy, and async replication can lose acknowledged writes on promotion even with `WAIT`. [Redis scripting](https://redis.io/docs/latest/develop/programmability/eval-intro/) · [eviction](https://redis.io/docs/latest/reference/eviction/) · [persistence](https://redis.io/docs/latest/management/persistence/) · [replication](https://redis.io/docs/latest/operate/oss_and_stack/management/replication/).

**Go/no-go:** require an independent durable domain uniqueness check or authoritative fallback that can recognize previous effects after Redis loses a record. Without that, a missing key after failure is indistinguishable from a new operation; the safe decision is not to advertise Redis as a durable standalone provider. If prototyped, use a dedicated pinned deployment profile, validate all state before mutation, preserve uncertain records rather than expiring them into a fresh claim, and prove same-key contention, fencing, memory exhaustion, eviction/misconfiguration, restart, promotion, lost acknowledgement and business-commit-before-result. Keep Redis transactional mode out of scope.
