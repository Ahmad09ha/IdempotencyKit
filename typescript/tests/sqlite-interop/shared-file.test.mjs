import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";
import { computeRequestFingerprint, computeScopeKey } from "@idempotencykit/core";
import { initializeSqliteDatabase, SqliteTransactionalExecutor } from "@idempotencykit/sqlite";

const run = promisify(execFile);
const dotnet = process.env.DOTNET_HOST_PATH || "dotnet";
const host = fileURLToPath(new URL(
  "../../../dotnet/tests/IdempotencyKit.Sqlite.InteropHost/bin/Release/net10.0/IdempotencyKit.Sqlite.InteropHost.dll",
  import.meta.url,
));
const operationId = "orders.create.v1";
const retention = 60 * 60 * 1000;

function database(t) {
  const dir = mkdtempSync(join(tmpdir(), "idempotencykit-sqlite-interop-"));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const file = join(dir, "orders.db");
  initializeSqliteDatabase(file);
  const db = new DatabaseSync(file);
  try {
    db.exec(`CREATE TABLE interop_orders (
      id INTEGER PRIMARY KEY, scope_key_hash TEXT NOT NULL, quantity INTEGER NOT NULL
    );`);
  } finally {
    db.close();
  }
  return file;
}

function identity(clientKey, quantity) {
  return {
    scopeKey: computeScopeKey({
      namespace: "sqlite-interop-test", operationId, principalId: "alice", tenantId: "acme", clientKey,
    }),
    fingerprint: computeRequestFingerprint({
      method: "POST", operationId, path: "/orders", bodyJson: JSON.stringify({ quantity }),
    }),
  };
}

async function executeNode(file, clientKey, quantity, busyTimeoutMs = 250) {
  const { scopeKey, fingerprint } = identity(clientKey, quantity);
  let callbackCalls = 0;
  const execution = await new SqliteTransactionalExecutor(file, busyTimeoutMs).execute(
    scopeKey, fingerprint, async session => {
      callbackCalls++;
      const result = session.prepare(
        "INSERT INTO interop_orders(scope_key_hash, quantity) VALUES (?, ?)",
      ).run(scopeKey, quantity);
      const id = Number(result.lastInsertRowid);
      return {
        statusCode: 201, contentType: "application/json",
        body: Buffer.from(JSON.stringify({ id, quantity, runtime: "node", note: "طلب" })),
        headers: { Location: `/orders/${id}` },
      };
    }, retention,
  );
  return {
    scopeKey, fingerprint, status: execution.status, callbackCalls,
    response: execution.response
      ? {
          ...execution.response,
          body: Buffer.from(execution.response.body).toString("base64"),
          headers: { ...execution.response.headers },
        }
      : null,
  };
}

async function executeDotnet(file, clientKey, quantity) {
  const result = await run(dotnet, [host, file, clientKey, String(quantity), "execute"], {
    timeout: 15_000,
  });
  return JSON.parse(result.stdout);
}

function verifyReplayAndFile(file, initial, replay, quantity, runtime) {
  assert.equal(initial.status, "executed");
  assert.equal(initial.callbackCalls, 1);
  assert.deepEqual(replay, { ...initial, status: "replayed", callbackCalls: 0 });
  const body = JSON.parse(Buffer.from(initial.response.body, "base64").toString("utf8"));
  assert.equal(initial.response.statusCode, 201);
  assert.equal(initial.response.headers.location, `/orders/${body.id}`);
  assert.deepEqual(body, { id: 1, quantity, runtime, note: "طلب" });

  const db = new DatabaseSync(file, { readOnly: true });
  try {
    const rows = db.prepare("SELECT * FROM interop_orders").all();
    assert.equal(rows.length, 1, "no second durable business effect");
    assert.equal(rows[0].id, body.id);
    assert.equal(rows[0].scope_key_hash, initial.scopeKey);
    assert.equal(rows[0].quantity, quantity);
    const records = db.prepare(
      "SELECT state, request_fingerprint, response_body FROM idempotencykit_records",
    ).all();
    assert.equal(records.length, 1);
    assert.equal(records[0].state, "completed");
    assert.equal(records[0].request_fingerprint, initial.fingerprint);
    assert.equal(Buffer.from(records[0].response_body).toString("base64"), initial.response.body);
  } finally {
    db.close();
  }
}

function verifyMismatch(initial, mismatch) {
  assert.equal(mismatch.scopeKey, initial.scopeKey);
  assert.notEqual(mismatch.fingerprint, initial.fingerprint);
  assert.equal(mismatch.status, "fingerprint_mismatch");
  assert.equal(mismatch.callbackCalls, 0);
  assert.equal(mismatch.response, null);
}

test("SQLite: Node commits an order and .NET replays the exact stored response", async t => {
  const file = database(t);
  const first = await executeNode(file, "shared-order-1", 3);
  verifyMismatch(first, await executeDotnet(file, "shared-order-1", 4));
  const replay = await executeDotnet(file, "shared-order-1", 3);
  verifyReplayAndFile(file, first, replay, 3, "node");
});

test("SQLite: .NET commits an order and Node replays the exact stored response", async t => {
  const file = database(t);
  const first = await executeDotnet(file, "shared-order-2", 7);
  verifyMismatch(first, await executeNode(file, "shared-order-2", 8));
  const replay = await executeNode(file, "shared-order-2", 7);
  verifyReplayAndFile(file, first, replay, 7, "dotnet");
});

test("SQLite: Node fails closed while .NET holds the file writer, then replays", async t => {
  const file = database(t);
  const worker = spawn(dotnet, [host, file, "shared-order-3", "11", "hold"], {
    stdio: ["pipe", "pipe", "pipe"], timeout: 15_000,
  });
  let stdout = "", stderr = "";
  let signalReady;
  const ready = new Promise(resolve => { signalReady = resolve; });
  const finished = new Promise(resolve => {
    worker.on("error", error => resolve({ code: null, error }));
    worker.on("close", (code, signal) => resolve({ code, signal }));
  });
  // If startup fails, stderr/exit below explains why; do not leave an unhandled EPIPE.
  worker.stdin.on("error", () => {});
  worker.stderr.setEncoding("utf8").on("data", chunk => { stderr += chunk; });
  worker.stdout.setEncoding("utf8").on("data", chunk => {
    stdout += chunk;
    if (stdout.includes("writer-held")) signalReady(true);
  });

  try {
    assert.equal(await Promise.race([ready, finished]), true, stderr);
    await assert.rejects(
      executeNode(file, "shared-order-3", 11, 50),
      /database is locked/i,
    );
    worker.stdin.end("commit\n");
    const stop = await finished;
    assert.equal(stop.code, 0, stop.error?.message || stderr || stop.signal);
    const first = JSON.parse(stdout.trim().split(/\r?\n/).at(-1));
    const replay = await executeNode(file, "shared-order-3", 11);
    verifyReplayAndFile(file, first, replay, 11, "dotnet");
  } finally {
    if (worker.exitCode === null && worker.signalCode === null) worker.kill();
    await finished;
  }
});
