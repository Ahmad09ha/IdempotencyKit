# SQLite File-backed Standard + Transactional core v1

**Status:** implemented on .NET 10 and Node.js 24, tested against **real temporary on-disk SQLite files**. This is a provider core, not a stable NuGet/npm release or production certification. SQLite-specific EF Core 10 and authenticated ASP.NET Core/NestJS Express HTTP adapters are **now implemented and tested in P4**. See [SQLite EF Core + HTTP v1](SQLITE_EFCORE_HTTP_V1.md).

The SQLite provider is intended for **one local host with a local database file** (potentially several cooperating processes on that same host). It is not a replacement for a PostgreSQL or SQL Server shared multi-host store. SQLite has a *single writer per database file*. WAL supports concurrent readers and a writer, not concurrent writers.

## Packages / source code

- .NET: `dotnet/src/IdempotencyKit.Sqlite` — `SqliteIdempotencyDatabase`, `SqliteIdempotencyStore : IIdempotencyStore`, `SqliteTransactionalExecutor : ITransactionalIdempotencyExecutor<SqliteConnection, SqliteTransaction>`; uses Microsoft.Data.Sqlite 10.0.4.
- Node.js 24+: `typescript/packages/sqlite` — `initializeSqliteDatabase`, `SqliteIdempotencyStore : IdempotencyStore`, `SqliteTransactionalExecutor : TransactionalIdempotencyExecutor<SqliteTransactionSession>`. Uses Node 24's built-in `node:sqlite` `DatabaseSync`; **no new native npm install**.
- Schema: [`spec/sql/sqlite/001_idempotencykit_records.sql`](../spec/sql/sqlite/001_idempotencykit_records.sql) — unique v1 scoped SHA-256 keys, owner and state, safe JSON replay bytes/headers, DB epoch-millisecond retention and completed-only expiry index.

The DB **must be initialized once before handling requests**. Both initializers set `PRAGMA journal_mode = WAL` and `PRAGMA synchronous = FULL`, then apply the schema. The app provides the directory (no automatic deletion). Connect by absolute or relative local file path, **not** `:memory:` or `file:` URI. Each provider checks WAL on opening, and sets synchronous FULL for its own connection. Multiple independent store instances can point to the **same actual local file** and share durable claims.

### .NET initialization + Standard mode

~~~csharp
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;

var file = Path.Combine(appDataDirectory, "orders-idempotency.db");
await SqliteIdempotencyDatabase.InitializeAsync(file);

IIdempotencyStore store = new SqliteIdempotencyStore(
    file, busyTimeoutSeconds: 2);

var claimed = await store.TryClaimAsync(
    scopeKeyHash, requestFingerprint, TimeSpan.FromSeconds(30), cancellationToken);

switch (claimed.Status)
{
    case IdempotencyClaimStatus.Acquired:
        // This owner may process the Standard-mode request. Complete only
        // with TryCompleteAsync(...claimed.OwnerToken.Value...) before replay.
        // An independently committed domain write is NOT made atomic here.
        break;
    case IdempotencyClaimStatus.Completed:
        // Recheck authorization and replay claimed.Response.
        break;
    case IdempotencyClaimStatus.InProgress:
    case IdempotencyClaimStatus.Uncertain:
        // Never reexecute without a trusted resolution.
        break;
    case IdempotencyClaimStatus.FingerprintMismatch:
        // Same client key, different body/operation: reject.
        break;
}
~~~

Owner tokens fence Standard-mode completion/lease renewal. Once the processing lease expires, its state becomes `Uncertain`, not automatically reacquired: the domain action might already have committed before a failure. Only expired **completed** records are purged. Never automatically purge `Uncertain`.

### .NET explicit same-transaction operation

~~~csharp
using IdempotencyKit.Core;
using IdempotencyKit.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json;

ITransactionalIdempotencyExecutor<SqliteConnection, SqliteTransaction> executor =
    new SqliteTransactionalExecutor(file);

var result = await executor.ExecuteAsync(
    scopeKeyHash, requestFingerprint,
    async (connection, transaction, ct) =>
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction; // MANDATORY: same SQLite transaction
        insert.CommandText =
            "INSERT INTO orders(product_id, quantity) VALUES (@p, @q) " +
            "RETURNING id";
        insert.Parameters.AddWithValue("@p", productId);
        insert.Parameters.AddWithValue("@q", quantity);
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + id });
    },
    TimeSpan.FromDays(1), cancellationToken);
~~~

The executor owns SQLite **BEGIN IMMEDIATE, callback, response capture, COMMIT**. The business callback must use the supplied `connection` and `transaction` for **all domain SQL**, and must never open another transaction or independently commit. Invalid response, callback failure, uncommitted connection loss => both domain SQL and provisional claim roll back. A committed result can be replayed by a new process after restart. As with any database, a broken COMMIT acknowledgement can be ambiguous; the consumer must retry **the same scoped key**, not a new one.

### Node.js 24 initialization + Standard mode

~~~typescript
import {
  initializeSqliteDatabase, SqliteIdempotencyStore,
} from "@idempotencykit/sqlite";

initializeSqliteDatabase("/var/lib/orders/idempotency.db");

const store = new SqliteIdempotencyStore(
  "/var/lib/orders/idempotency.db", 250); // busy timeout in MILLISECONDS
const claimed = await store.tryClaim(scopeKeyHash, requestFingerprint, 30_000);
~~~

### Node.js explicit same-transaction operation

~~~typescript
import { SqliteTransactionalExecutor } from "@idempotencykit/sqlite";

const executor = new SqliteTransactionalExecutor(
  "/var/lib/orders/idempotency.db", 250);

const result = await executor.execute(
  scopeKeyHash, requestFingerprint,
  async session => {
    // Do not open a new DatabaseSync or another transaction here.
    // The session exposes only a guarded prepare(), not COMMIT/PRAGMA.
    const inserted = session.prepare(
      "INSERT INTO orders(product_id,quantity) VALUES (?,?)",
    ).run(productId, quantity);
    const id = Number(inserted.lastInsertRowid);
    return {
      statusCode: 201,
      contentType: "application/json",
      body: Buffer.from(JSON.stringify({ id })),
      headers: { Location: "/orders/" + id },
    };
  },
  86_400_000, // retention milliseconds
);
~~~

The Node 24 `DatabaseSync` driver runs SQLite calls **synchronously**. The adapter serializes its own operations on the resolved file path **inside one Node process**, so an async callback awaiting a Promise cannot be interrupted by another operation from this adapter which would synchronously block waiting for its write lock. This in-process gate does **not** coordinate other programs, processes, raw `DatabaseSync` connections, or other machines. SQLite's real file lock still controls cross-process writes. The Node session only exposes a restricted `prepare(sql)` and rejects transaction-control SQL. Do not hold the write lock across slow `await`, external I/O, network requests or user input. Long callbacks will block other SQLite writers and may cause storage failures.

## Critical busy / locked semantics

SQLite `BEGIN IMMEDIATE` reserves the one writer for the **entire file**, even when concurrent requests have **different idempotency keys**. SQLite WAL permits reads while the writer holds the lock, but does not permit a second writer.

- **A committed processing row** with the same scoped hash/fingerprint => known `InProgress` result.
- **Any `SQLITE_BUSY` / `SQLITE_LOCKED` from a file-wide writer** => a thrown **storage error**, even if the request uses the same key. The provider cannot prove the lock owner's identity. **Never return `InProgress` based only on a busy/locked exception.**
- Handle storage errors at the eventual HTTP boundary as **fail-closed** (normally 503) with bounded retry using the **same key**. Never automatically rerun the domain callback on an unknown COMMIT outcome.
- .NET defaults to a **2-second** busy timeout; Node defaults to **250 milliseconds**. Both configurable (max 30 seconds), and may wait or temporarily block on another writer. Keep SQL transactions short.
- A processing lease expiry is not proof that a business Standard-mode write was rolled back. Expired claims become `Uncertain` until reconciled.

## Durability & deployment restrictions

- A supported deployment is **one host using a local filesystem** with trustworthy OS locking and fsync semantics. Multiple workers/processes on that same local file can coordinate via SQLite locks, with single-writer throughput and bounded errors. **NFS/SMB/network-mounted WAL, multi-host replicas, multi-region and distributed SQLite-backed deduplication are not supported.**
- Keep **WAL (`.db-wal`) and shared-memory (`.db-shm`) companion files** together on the same filesystem while the database is live. Never manually copy just the `.db` file while the app is running and assume committed data is included.
- Use SQLite's **online backup API** or safely coordinated checkpoints/quiescent snapshots; back up SQLite state **and domain state consistently** if disaster-recovery guarantees are required.
- Use appropriate local file ownership, filesystem permissions, disk health, free-space, backup validation and encryption-at-rest controls for user data. Avoid raw idempotency keys or PII in logs.
- After a crash, WAL recovery is handled by SQLite on next open. Do **not** delete `.db-wal`/`.db-shm` files to try to repair unknown outcomes.
- SQLite time/expiry uses the **database engine's epoch millisecond clock** (`unixepoch('subsec')`). Verify host clock health; this does not implement distributed clock synchronization.
- Maximum lease **1 day**, response retention **365 days**, JSON response **256 KiB**, safe replay headers **Location, ETag, Content-Language**. Authorization must be checked on every HTTP replay.

## Evidence, guarantees and boundaries

The dedicated CI runs temporary **actual file-backed** SQLite databases in .NET 10 and Node 24. It covers owner fencing, replays after fresh connection, mismatch isolation, expired/uncertain state, cleanup, invalid-header rejection, domain callback exception rollback, **100 same-key concurrent operations** and **unrelated process writer → SQLITE_BUSY**. The Node suite launches independent worker processes and uses **SIGKILL before and after COMMIT** with same-key retries.

**P4 now implemented:** SQLite EF Core 10, ASP.NET Core/NestJS Express transactional HTTP endpoints with real-file tests and NestJS HTTP SIGKILL before/after COMMIT. **Still not implemented:** full .NET HTTP process kill, multi-host sharing, native backup/restore tooling, publishable NuGet/npm releases or generalized reconciliation/observability.

P4 has added SQLite EF Core and explicit HTTP adapters, preserving this file/locality policy. For run-specific results see [Verification](VERIFICATION.md) and [Provider Rollout](DATABASE_PROVIDER_ROADMAP.md).
