import assert from "node:assert/strict";
import { mkdtempSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { backup, DatabaseSync } from "node:sqlite";
import test from "node:test";
import { computeRequestFingerprint, computeScopeKey } from "@idempotencykit/core";
import { initializeSqliteDatabase, SqliteTransactionalExecutor } from "@idempotencykit/sqlite";

function fixture(t) {
  const folder = mkdtempSync(join(tmpdir(), "idempotencykit-sqlite-maintenance-"));
  const file = join(folder, "orders.db");
  t.after(() => rmSync(folder, { recursive: true, force: true }));
  initializeSqliteDatabase(file);
  const db = new DatabaseSync(file, { timeout: 50 });
  db.exec("CREATE TABLE orders(id INTEGER PRIMARY KEY, scope_key_hash TEXT NOT NULL)");
  return { file, folder, db };
}

function identity(clientKey) {
  return {
    key: computeScopeKey({
      namespace: "sqlite-maintenance-test", operationId: "orders.create.v1",
      principalId: "alice", clientKey,
    }),
    fingerprint: computeRequestFingerprint({
      method: "POST", operationId: "orders.create.v1", path: "/orders", bodyJson: "{}",
    }),
  };
}

function write(file, id, afterInsert = async () => {}) {
  return new SqliteTransactionalExecutor(file).execute(id.key, id.fingerprint, async session => {
    const created = session.prepare("INSERT INTO orders(scope_key_hash) VALUES (?)").run(id.key);
    await afterInsert();
    const orderId = Number(created.lastInsertRowid);
    return {
      statusCode: 201,
      contentType: "application/json",
      body: Buffer.from(JSON.stringify({ id: orderId })),
      headers: { location: `/orders/${orderId}` },
    };
  }, 3_600_000);
}

async function assertReplay(file, identity, initial) {
  const retry = await new SqliteTransactionalExecutor(file).execute(
    identity.key, identity.fingerprint, async () => assert.fail("Replay must not write"), 3_600_000,
  );
  assert.equal(retry.status, "replayed");
  assert.equal(retry.response.statusCode, initial.response.statusCode);
  assert.equal(retry.response.contentType, initial.response.contentType);
  assert.deepEqual(retry.response.headers, initial.response.headers);
  assert.deepEqual(Buffer.from(retry.response.body), Buffer.from(initial.response.body));
}

function count(db, table) {
  assert.ok(["orders", "idempotencykit_records"].includes(table));
  return db.prepare(`SELECT COUNT(*) AS total FROM ${table}`).get().total;
}

test("online backup restores committed WAL-backed order and replay atomically", { timeout: 10_000 }, async t => {
  const { file, folder, db } = fixture(t);
  const restored = join(folder, "restored.db");
  const beforeBackup = identity("before-backup");
  const crossingBackup = identity("crossing-backup");
  try {
    const completed = await write(file, beforeBackup);
    assert.equal(completed.status, "executed");
    assert.ok(statSync(file + "-wal").size > 0, "fixture must include live WAL frames");

    let notifyInserted, allowCommit;
    const inserted = new Promise(resolve => { notifyInserted = resolve; });
    const commit = new Promise(resolve => { allowCommit = resolve; });
    const writing = write(file, crossingBackup, async () => {
      notifyInserted();
      await commit;
    });
    try {
      // Wait until the second order is written but uncommitted in another
      // connection. The backup must not contain that order or its claim.
      await Promise.race([inserted, writing.then(() => assert.fail("Expected writer to hold"))]);
      assert.ok(await backup(db, restored) > 0);
    } finally {
      allowCommit();
      await writing;
    }
    assert.equal(count(db, "orders"), 2);
    assert.equal(count(db, "idempotencykit_records"), 2);

    // Reapply the normal local-file startup configuration on the restored file.
    initializeSqliteDatabase(restored);
    const inspect = new DatabaseSync(restored, { readOnly: true });
    try {
      assert.equal(inspect.prepare("PRAGMA integrity_check").get().integrity_check, "ok");
      assert.equal(count(inspect, "orders"), 1);
      assert.equal(count(inspect, "idempotencykit_records"), 1);
      assert.equal(inspect.prepare("SELECT scope_key_hash FROM orders").get().scope_key_hash, beforeBackup.key);
      assert.equal(inspect.prepare("SELECT state FROM idempotencykit_records").get().state, "completed");
    } finally {
      inspect.close();
    }
    await assertReplay(restored, beforeBackup, completed);
    assert.equal((await write(restored, crossingBackup)).status, "executed",
      "the uncommitted operation was not part of the backup");
  } finally {
    db.close();
  }
});

test("WAL truncate checkpoint reports a pinned reader, then preserves completed replay", async t => {
  const { file, db } = fixture(t);
  const reader = new DatabaseSync(file, { readOnly: true });
  try {
    reader.exec("BEGIN");
    assert.equal(count(reader, "orders"), 0); // pin a snapshot before the write
    const order = identity("checkpoint");
    const result = await write(file, order);
    assert.equal(result.status, "executed");
    assert.equal(count(reader, "orders"), 0);
    assert.equal(count(db, "orders"), 1);

    const blocked = db.prepare("PRAGMA wal_checkpoint(TRUNCATE)").get();
    assert.equal(blocked.busy, 1);
    assert.ok(blocked.log > blocked.checkpointed);
    assert.ok(statSync(file + "-wal").size > 0);
    reader.exec("ROLLBACK");

    const checkpoint = db.prepare("PRAGMA wal_checkpoint(TRUNCATE)").get();
    assert.deepEqual({ ...checkpoint }, { busy: 0, log: 0, checkpointed: 0 });
    assert.equal(statSync(file + "-wal").size, 0);
    await assertReplay(file, order, result);
    assert.equal(count(db, "orders"), 1);
    assert.equal(count(db, "idempotencykit_records"), 1);
  } finally {
    reader.close();
    db.close();
  }
});
