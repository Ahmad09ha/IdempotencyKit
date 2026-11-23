import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import pg from "pg";
import { computeScopeKey, computeRequestFingerprint } from "@idempotencykit/core";
import { PostgresIdempotencyStore } from "@idempotencykit/postgres";

const connectionString = process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL;
const pool = connectionString ? new pg.Pool({ connectionString, max: 16 }) : null;
const store = pool ? new PostgresIdempotencyStore(pool) : null;

before(async () => {
  if (pool) {
    const sql = readFileSync(fileURLToPath(new URL(
      "../../spec/sql/postgres/001_idempotencykit_records.sql", import.meta.url)), "utf8");
    const connection = await pool.connect();
    try {
      await connection.query("BEGIN");
      await connection.query(
        "SELECT pg_advisory_xact_lock(hashtext('idempotencykit_schema_001'))");
      await connection.query(sql);
      await connection.query("COMMIT");
    } catch (error) {
      await connection.query("ROLLBACK");
      throw error;
    } finally {
      connection.release();
    }
  }
});

after(async () => { if (pool) await pool.end(); });
const integration = (name, fn) => test(name, { skip: !pool }, fn);
const key = () => computeScopeKey({
  namespace: "test", operationId: "orders.create.v1",
  principalId: "user-" + randomUUID(), clientKey: "key-" + randomUUID(),
});
const fingerprint = bodyJson => computeRequestFingerprint({
  method: "POST", operationId: "orders.create.v1", path: "/orders", bodyJson,
});
const response = () => ({
  statusCode: 201, contentType: "application/json; charset=utf-8",
  body: Buffer.from('{"id":1001}'), headers: { Location: "/orders/1001" },
});

integration("claim, in-progress conflict, mismatch, owner fencing and replay", async () => {
  const id = key(), hash = fingerprint('{"quantity":2}');
  const claimed = await store.tryClaim(id, hash, 20_000);
  assert.equal(claimed.status, "acquired");
  assert.ok(claimed.ownerToken);
  assert.equal((await store.tryClaim(id, hash, 20_000)).status, "in_progress");
  assert.equal((await store.tryClaim(id, fingerprint('{"quantity":3}'), 20_000)).status,
    "fingerprint_mismatch");
  assert.equal(await store.tryComplete(id, hash, randomUUID(), response(), 3_600_000), false);
  assert.equal(await store.tryComplete(id, hash, claimed.ownerToken, response(), 3_600_000), true);
  assert.equal(await store.tryComplete(id, hash, claimed.ownerToken, response(), 3_600_000), false);
  const replay = await store.tryClaim(id, hash, 20_000);
  assert.equal(replay.status, "completed");
  assert.equal(replay.response.statusCode, 201);
  assert.equal(Buffer.from(replay.response.body).toString(), '{"id":1001}');
  assert.equal(replay.response.headers.location, "/orders/1001");
});

integration("32 concurrent claims have exactly one owner", async () => {
  const id = key(), hash = fingerprint('{"quantity":2}');
  const responses = await Promise.all(Array.from({ length: 32 }, () =>
    store.tryClaim(id, hash, 60_000)));
  assert.equal(responses.filter(x => x.status === "acquired").length, 1);
  assert.equal(responses.filter(x => x.status === "in_progress").length, 31);
  const row = await pool.query("SELECT count(*) AS count FROM idempotencykit_records WHERE scope_key_hash = $1", [id]);
  assert.equal(Number(row.rows[0].count), 1);
});

integration("expired lease becomes uncertain; stale owner cannot finish", async () => {
  const id = key(), hash = fingerprint('{"quantity":1}');
  const claim = await store.tryClaim(id, hash, 40);
  await new Promise(resolve => setTimeout(resolve, 180));
  assert.equal((await store.tryClaim(id, hash, 1000)).status, "uncertain");
  assert.equal((await store.tryClaim(id, hash, 1000)).status, "uncertain");
  assert.equal(await store.tryExtendLease(id, claim.ownerToken, 10_000), false);
  assert.equal(await store.tryComplete(id, hash, claim.ownerToken, response(), 1_000), false);
});

integration("current owner can extend its lease", async () => {
  const id = key(), hash = fingerprint('{"quantity":5}');
  const claim = await store.tryClaim(id, hash, 5_000);
  assert.equal(await store.tryExtendLease(id, randomUUID(), 10_000), false);
  assert.equal(await store.tryExtendLease(id, claim.ownerToken, 10_000), true);
  assert.equal(await store.tryComplete(id, hash, claim.ownerToken, response(), 50_000), true);
});

integration("unsafe headers and oversized bodies fail before storage", async () => {
  const id = key(), hash = fingerprint('{"quantity":6}');
  const claim = await store.tryClaim(id, hash, 5_000);
  await assert.rejects(() => store.tryComplete(id, hash, claim.ownerToken,
    { ...response(), headers: { "Set-Cookie": "secret=1" } }, 1000));
  await assert.rejects(() => store.tryComplete(id, hash, claim.ownerToken,
    { ...response(), body: new Uint8Array(262145) }, 1000));
  await assert.rejects(() => store.tryComplete(id, hash, claim.ownerToken,
    { ...response(), contentType: "application/json;\r\nset-cookie: secret=1" }, 1000));
  assert.equal(await store.tryComplete(id, hash, claim.ownerToken, response(), 1000), true);
});

integration("only completed expired records are purged", async () => {
  const id = key(), hash = fingerprint('{"quantity":7}');
  const claim = await store.tryClaim(id, hash, 5_000);
  assert.equal(await store.tryComplete(id, hash, claim.ownerToken, response(), 20), true);
  await new Promise(resolve => setTimeout(resolve, 150));
  assert.ok(await store.purgeExpiredCompleted(1000) >= 1);
  const count = await pool.query("SELECT count(*) AS count FROM idempotencykit_records WHERE scope_key_hash = $1", [id]);
  assert.equal(Number(count.rows[0].count), 0);
  assert.equal((await store.tryClaim(id, hash, 5000)).status, "acquired");
});
