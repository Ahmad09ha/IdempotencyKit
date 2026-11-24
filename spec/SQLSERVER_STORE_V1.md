# SQL Server 2022 / Azure SQL — Standard and Transactional core v1

**Implementation:** `IdempotencyKit.SqlServer` (.NET 10) and `@idempotencykit/sqlserver` (Node.js/TypeScript). **Status: early provider implementation; validation is tracked in `docs/VERIFICATION.md`.** This is not yet a published or production-certified package. SQL Server EF Core and ASP.NET Core/NestJS provider-specific HTTP integrations are a separate next milestone.

Unlike PostgreSQL, SQL Server does not use `INSERT ... ON CONFLICT` and has different transaction/locking behavior. This provider uses **transaction-owned, scoped-key application locks** with a durable unique key index. Business and idempotency writes still must use the **same physical SQL transaction**.

## Required migration

Apply [`001_idempotencykit_records.sql`](sql/sqlserver/001_idempotencykit_records.sql) to the **application database** before enabling the provider. It creates `dbo.idempotencykit_records` (binary-collated unique `scope_key_hash`, owner and state, UTC timestamps, bounded replay body, safe-header JSON and explicit retention index).

Migration may be rerun after it has completed. In a multi-instance rollout, apply migrations using a single schema migration owner. The SQL migration does not use `GO` and can be executed with SQL Client / node-mssql as one batch.

The provider needs SELECT/INSERT/UPDATE/DELETE rights on its table and permission to invoke `sys.sp_getapplock` in the **same database**. The transaction-owned application lock resource is `IdempotencyKit:v1:<scopeKeyHash>`, and both .NET and Node use that exact string. All app instances must connect to **one authoritative database** for deduplication.

## .NET Standard mode

~~~csharp
using IdempotencyKit.Core;
using IdempotencyKit.SqlServer;

var store = new SqlServerIdempotencyStore(connectionString);
IIdempotencyStore standardStore = store;

var claim = await standardStore.TryClaimAsync(
    scopeKeyHash, fingerprint, TimeSpan.FromMinutes(1), cancellationToken);

switch (claim.Status)
{
    case IdempotencyClaimStatus.Acquired:
        // Only this request owns the new Standard-mode claim.
        // Execute the handler, then persist a validated bounded 2xx JSON
        // response using TryCompleteAsync(..., claim.OwnerToken.Value,...).
        break;
    case IdempotencyClaimStatus.Completed:
        // Re-authorize and replay claim.Response, do NOT run the handler.
        break;
    case IdempotencyClaimStatus.FingerprintMismatch:
        // Return HTTP 422, do not execute.
        break;
    case IdempotencyClaimStatus.InProgress:
    case IdempotencyClaimStatus.Uncertain:
        // Return a safe 409 or explicit reconciliation status; never rerun.
        break;
}
~~~

Standard mode **does not** make separately committed business writes atomic. Failures after business COMMIT but before completing the key can leave an uncertain outcome. Claims use owner tokens; expired processing records become `Uncertain` and are **not** auto-reclaimed. Cleanup deletes expired **completed** records only.

## .NET Transactional mode

The provider implements `ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction>`.

~~~csharp
using System.Text.Json;
using IdempotencyKit.Core;
using IdempotencyKit.SqlServer;
using Microsoft.Data.SqlClient;

ITransactionalIdempotencyExecutor<SqlConnection, SqlTransaction> executor =
    new SqlServerTransactionalExecutor(connectionString);

var result = await executor.ExecuteAsync(
    scopeKeyHash,
    fingerprint,
    async (connection, transaction, ct) =>
    {
        await using var command = new SqlCommand(
            "INSERT INTO dbo.orders(product_id, quantity) " +
            "OUTPUT INSERTED.id VALUES (@product, @quantity)",
            connection, transaction); // SAME transaction
        command.Parameters.AddWithValue("@product", productId);
        command.Parameters.AddWithValue("@quantity", quantity);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;

        return new StoredHttpResponse(
            201, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new { id, quantity }),
            new Dictionary<string, string> { ["Location"] = "/orders/" + id });
    },
    TimeSpan.FromDays(1),
    cancellationToken);

// Executed/Replayed => result.Response
// InProgress => retry same key; FingerprintMismatch => reject;
// Uncertain => manual/domain reconciliation.
~~~

The callback **must not** start, commit or roll back a different transaction or call a repository with an independent `SqlConnection`. The executor owns BEGIN, a transaction-owned `sp_getapplock`, domain work, response storage, COMMIT, rollback and replay. **Do not add Standard-mode `[Idempotent]` to this operation**.

## Node.js/TypeScript Transactional mode

Use `mssql` with the Tedious driver. This requires a connected, app-owned connection pool. A transaction-bound request is always `new sql.Request(transaction)`, **not** `pool.request()`.

~~~typescript
import sql from "mssql";
import { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";

const pool = await new sql.ConnectionPool({
  server: process.env.SQLSERVER_HOST!,
  database: process.env.SQLSERVER_DATABASE!,
  user: process.env.SQLSERVER_USER!,
  password: process.env.SQLSERVER_PASSWORD!,
  options: {
    encrypt: true,
    // Production: validate certificates; do NOT use trustServerCertificate:true
  },
}).connect();

const executor = new SqlServerTransactionalExecutor(pool);

const result = await executor.execute(
  scopeKeyHash, fingerprint,
  async transaction => {
    const inserted = await new sql.Request(transaction)
      .input("product", sql.Int, productId)
      .input("quantity", sql.Int, quantity)
      .query(
        "INSERT INTO dbo.orders(product_id, quantity) " +
        "OUTPUT INSERTED.id VALUES (@product, @quantity)");
    const id = inserted.recordset[0].id;
    return {
      statusCode: 201,
      contentType: "application/json",
      body: Buffer.from(JSON.stringify({ id, quantity })),
      headers: { Location: "/orders/" + id },
    };
  },
  86_400_000, // 24 hours, milliseconds
);
~~~

The Node Standard-mode store has the same basic interface as the existing `@idempotencykit/postgres` store: `tryClaim`, `tryComplete`, `tryExtendLease`, `tryMarkUncertain` and `purgeExpiredCompleted`. It uses the same SQL Server table and lock resource as the Transactional executor.

## Lock and failure handling

Under the hood, `sp_getapplock` is requested with `@LockMode='Exclusive'`, `@LockOwner='Transaction'` and a bounded `@LockTimeout` (2 seconds by default). Resource names use the **already hashed** key; they do not include raw client keys or user PII.

- Nonnegative return: current SQL transaction owns the key resource; read or insert the unique claim.
- `-1` timeout: **same-key** contention, so the business callback is NOT run and the method returns `InProgress`.
- `-2` canceled, `-3` deadlock, `-999` parameter/configuration error, SQL exceptions: **fail closed**; no automatic callback execution.
- Business callback throws or response validation fails: rollback both order changes and the provisional claim.
- COMMIT succeeds but the response is lost: retry with **same scoped key + fingerprint** against the authoritative database, and return persisted replay data.
- COMMIT acknowledgement is ambiguous: propagate an error, never auto-reexecute a callback.
- Existing Standard-mode processing/uncertain rows prevent transactional execution; completed rows replay.

SQL Server 2022 `DATEADD` has 32-bit integer arguments for large durations. The provider splits retention and lease durations into **seconds + milliseconds** so long retention windows do not overflow an int. Durations are bounded: lease ≤ 1 day, retained response ≤ 365 days.

**Response rules:** only 2xx, valid UTF-8 JSON (up to 256 KiB), content types `application/json` / `application/problem+json` and safe response headers `Location` / `ETag` / `Content-Language`. Each provider validates before COMMIT. No arbitrary cookies, auth tokens or streaming responses are replayed.

## Deployment and security limits

- **Durability:** configure SQL Server database backups, primary/synchronous replication, recovery model, deadlock/error monitoring and permissions appropriately. Failover is not automatically exactly-once.
- **Azure SQL:** verify transaction-owned application locks and access/timeout behavior against your chosen Azure SQL tier; Docker SQL Server 2022 CI is not Azure SQL certification.
- **Identity:** SHA-256 scoped keys and fingerprints are computed from trusted authenticated principal/tenant and original request JSON using the **shared v1 core**. Database selection never changes that contract.
- **Authorization:** authorization must run on **every request including replay**; a saved response is not permission to access it.
- **External effects:** sending payments, emails, cloud messages or committing other databases is outside this single SQL transaction.
- **Migration/cutover:** moving a tenant from PostgreSQL to SQL Server without migrating claim history can permit a previously completed key to be treated as new. Use an explicit authoritative cutover plan.
- **Tests:** use an isolated SQL Server 2022 CI database, not the production `master` database. The CI runner connects to disposable `master` only to avoid provisioning an extra test database; real applications should use their own app database.
- **TLS:** `TrustServerCertificate=True` is allowed only in the disposable CI test connection; use verified server certificates in production.

For the next layers (EF Core SQL Server and transactional ASP.NET Core/NestJS), see [Database Provider Roadmap](../docs/DATABASE_PROVIDER_ROADMAP.md). For current CI evidence, see [Verification Evidence](../docs/VERIFICATION.md).
