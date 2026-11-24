import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import sql from "mssql";
import {
  computeRequestFingerprint, computeScopeKey,
} from "@idempotencykit/core";
import {
  SqlServerIdempotencyStore, SqlServerTransactionalExecutor,
} from "@idempotencykit/sqlserver";

const enabled = Boolean(process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PASSWORD);
const config = {
  server: process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_HOST ?? "127.0.0.1",
  port: Number(process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PORT ?? "1433"),
  database: "master",
  user: "sa",
  password: process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PASSWORD ?? "",
  options: { encrypt: true, trustServerCertificate: true },
  pool: { max: 36, min: 0, idleTimeoutMillis: 30_000 },
  connectionTimeout: 5_000,
  requestTimeout: 30_000,
};
let pool, store, executor;
const integration = (name, fn) => test(name, { skip: !enabled }, fn);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

before(async () => {
  if (!enabled) return;
  let error;
  for (let i = 0; i < 100; i++) {
    const attempt = new sql.ConnectionPool(config);
    try {
      await attempt.connect();
      pool = attempt;
      break;
    } catch (failure) {
      error = failure;
      try { await attempt.close(); } catch {}
      await delay(1000);
    }
  }
  if (!pool) throw new Error("SQL Server did not start: " + error?.message);
  const migration = readFileSync(new URL(
    "../../spec/sql/sqlserver/001_idempotencykit_records.sql",
    import.meta.url), "utf8");
  await pool.request().batch(migration);
  await pool.request().batch(`
    IF OBJECT_ID(N'dbo.idempotencykit_sqlserver_orders_test', N'U') IS NULL
      EXEC(N'CREATE TABLE dbo.idempotencykit_sqlserver_orders_test
        (id bigint IDENTITY(1,1) PRIMARY KEY,
         scope_key_hash nvarchar(67) NOT NULL, quantity int NOT NULL)')`);
  store = new SqlServerIdempotencyStore(pool);
  executor = new SqlServerTransactionalExecutor(pool);
});
after(async () => { if (pool) await pool.close(); });

const key = () => computeScopeKey({
  namespace: "sqlserver-ts-test",
  operationId: "orders.create.v1",
  principalId: "user-" + randomUUID(),
  tenantId: "tenant-a",
  clientKey: "order-" + randomUUID(),
});
const fingerprint = quantity => computeRequestFingerprint({
  method: "POST", operationId: "orders.create.v1",
  path: "/orders", bodyJson: JSON.stringify({ quantity }),
});
const response = (id, quantity) => ({
  statusCode: 201, contentType: "application/json; charset=utf-8",
  body: Buffer.from(JSON.stringify({ id, quantity })),
  headers: { Location: "/orders/" + id },
});
async function count(table, scope) {
  if (!["idempotencykit_sqlserver_orders_test", "idempotencykit_records"].includes(table))
    throw new Error("Unsafe test table");
  const result = await pool.request()
    .input("key", sql.NVarChar(67), scope)
    .query("SELECT COUNT_BIG(*) AS value FROM dbo." + table +
      " WHERE scope_key_hash = @key");
  return Number(result.recordset[0].value);
}
async function insertOrder(transaction, scope, qty) {
  const result = await new sql.Request(transaction)
    .input("key", sql.NVarChar(67), scope)
    .input("qty", sql.Int, qty)
    .query(`
      INSERT INTO dbo.idempotencykit_sqlserver_orders_test
        (scope_key_hash, quantity) OUTPUT INSERTED.id
      VALUES (@key, @qty)`);
  return response(Number(result.recordset[0].id), qty);
}

integration("Standard mode claims, fences owners and replays safe bytes", async () => {
  const scope = key(), fp = fingerprint(1);
  const claim = await store.tryClaim(scope, fp, 60_000);
  assert.equal(claim.status, "acquired");
  assert.equal((await store.tryClaim(scope, fp, 60_000)).status, "in_progress");
  assert.equal(await store.tryComplete(scope, fp, randomUUID(), response(2, 1),
    30_000), false);
  assert.equal(await store.tryExtendLease(scope, randomUUID(), 30_000), false);
  assert.equal(await store.tryExtendLease(scope, claim.ownerToken, 30_000), true);
  assert.equal(await store.tryComplete(scope, fp, claim.ownerToken,
    response(2, 1), 90 * 86_400_000), true);
  const replay = await store.tryClaim(scope, fp, 60_000);
  assert.equal(replay.status, "completed");
  assert.deepEqual(Buffer.from(replay.response.body), response(2, 1).body);
  assert.equal(replay.response.headers.location, "/orders/2");
  assert.equal((await store.tryClaim(scope, fingerprint(2), 60_000)).status,
    "fingerprint_mismatch");
});

integration("Standard mode expired claim becomes uncertain, never reclaimed", async () => {
  const scope = key(), fp = fingerprint(2);
  const claimed = await store.tryClaim(scope, fp, 25);
  assert.equal(claimed.status, "acquired");
  await delay(130);
  assert.equal((await store.tryClaim(scope, fp, 60_000)).status, "uncertain");
  assert.equal(await store.tryComplete(scope, fp, claimed.ownerToken,
    response(2, 2), 60_000), false);
  assert.equal((await store.tryClaim(scope, fp, 60_000)).status, "uncertain");
});

integration("Transactional mode commits business order and replay response once", async () => {
  const scope = key(), fp = fingerprint(3);
  let calls = 0;
  const first = await executor.execute(scope, fp, async transaction => {
    calls++;
    return insertOrder(transaction, scope, 3);
  }, 90 * 86_400_000);
  assert.equal(first.status, "executed");
  assert.equal(await count("idempotencykit_sqlserver_orders_test", scope), 1);
  assert.equal(await count("idempotencykit_records", scope), 1);
  const replay = await new SqlServerTransactionalExecutor(pool).execute(
    scope, fp, async () => { throw new Error("Must not reexecute."); }, 86_400_000);
  assert.equal(replay.status, "replayed");
  assert.deepEqual(Buffer.from(replay.response.body), Buffer.from(first.response.body));
  assert.equal(replay.response.headers.location, first.response.headers.location);
  assert.equal(calls, 1);
  assert.equal((await executor.execute(scope, fingerprint(4), async () => {
    throw new Error("Mismatched callback must not run.");
  }, 86_400_000)).status, "fingerprint_mismatch");
  const fromStandard = await store.tryClaim(scope, fp, 60_000);
  assert.equal(fromStandard.status, "completed");
  assert.deepEqual(Buffer.from(fromStandard.response.body), Buffer.from(first.response.body));
});

integration("Throw/invalid response rolls both SQL effects back", async () => {
  const scope = key(), fp = fingerprint(5);
  await assert.rejects(() => executor.execute(scope, fp, async transaction => {
    await insertOrder(transaction, scope, 5);
    throw new Error("Domain exception after SQL insert.");
  }, 86_400_000));
  assert.equal(await count("idempotencykit_sqlserver_orders_test", scope), 0);
  assert.equal(await count("idempotencykit_records", scope), 0);
  await assert.rejects(() => executor.execute(scope, fp, async transaction => {
    const result = await insertOrder(transaction, scope, 5);
    return { ...result, headers: { "Set-Cookie": "unsafe=1" } };
  }, 86_400_000));
  assert.equal(await count("idempotencykit_sqlserver_orders_test", scope), 0);
  assert.equal(await count("idempotencykit_records", scope), 0);
  const good = await executor.execute(scope, fp,
    tx => insertOrder(tx, scope, 5), 86_400_000);
  assert.equal(good.status, "executed");
  assert.equal(await count("idempotencykit_sqlserver_orders_test", scope), 1);
});

integration("100 concurrent same-key callbacks commit exactly one order", async () => {
  const scope = key(), fp = fingerprint(6);
  const runner = new SqlServerTransactionalExecutor(pool, 8_000);
  let calls = 0;
  const result = await Promise.all(Array.from({ length: 100 }, () =>
    runner.execute(scope, fp, async transaction => {
      calls++;
      await delay(80);
      return insertOrder(transaction, scope, 6);
    }, 86_400_000)));
  assert.equal(result.filter(item => item.status === "executed").length, 1);
  assert.ok(result.every(item => ["executed","replayed","in_progress"].includes(item.status)));
  assert.equal(calls, 1);
  assert.equal(await count("idempotencykit_sqlserver_orders_test", scope), 1);
  assert.equal(await count("idempotencykit_records", scope), 1);
});

integration("same-key lock timeout returns in_progress without executing callback", async () => {
  const scope = key(), fp = fingerprint(8);
  let start;
  const started = new Promise(resolve => { start = resolve; });
  let unblock;
  const held = new Promise(resolve => { unblock = resolve; });
  let calls = 0;
  const short = new SqlServerTransactionalExecutor(pool, 40);
  const first = short.execute(scope, fp, async transaction => {
    calls++;
    start();
    await held;
    return insertOrder(transaction, scope, 8);
  }, 86_400_000);
  await started;
  try {
    const second = await short.execute(scope, fp, async () => {
      calls++;
      throw new Error("Busy transaction must not execute second callback.");
    }, 86_400_000);
    assert.equal(second.status, "in_progress");
    assert.equal(calls, 1);
  } finally {
    unblock();
  }
  assert.equal((await first).status, "executed");
});

integration("Processing and uncertain Standard claims block Transactional callbacks", async () => {
  const scope = key(), fp = fingerprint(7);
  const owned = await store.tryClaim(scope, fp, 60_000);
  assert.equal(owned.status, "acquired");
  assert.equal((await executor.execute(scope, fp, async () => {
    throw new Error("Standard owner has this key.");
  }, 86_400_000)).status, "in_progress");
  assert.equal(await store.tryMarkUncertain(scope, fp, owned.ownerToken), true);
  assert.equal((await executor.execute(scope, fp, async () => {
    throw new Error("Unknown outcome cannot reexecute.");
  }, 86_400_000)).status, "uncertain");
});

integration("Cleanup deletes expired completed records without uncertain records", async () => {
  const completedKey = key(), uncertainKey = key();
  const old = await store.tryClaim(completedKey, fingerprint(9), 60_000);
  assert.equal(await store.tryComplete(completedKey, fingerprint(9),
    old.ownerToken, response(4, 9), 1), true);
  const unclear = await store.tryClaim(uncertainKey, fingerprint(10), 60_000);
  assert.equal(await store.tryMarkUncertain(uncertainKey, fingerprint(10),
    unclear.ownerToken), true);
  await delay(120);
  assert.ok((await store.purgeExpiredCompleted(100)) >= 1);
  assert.equal(await count("idempotencykit_records", completedKey), 0);
  assert.equal(await count("idempotencykit_records", uncertainKey), 1);
});
