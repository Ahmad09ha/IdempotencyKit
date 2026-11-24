import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { fork } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";
import "reflect-metadata";
import {
  Controller, Post, Module, Injectable, Inject, Req, Res, UseGuards,
} from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import {
  initializeSqliteDatabase, SqliteIdempotencyStore, SqliteTransactionalExecutor,
} from "@idempotencykit/sqlite";
import {
  computeScopeKey, computeRequestFingerprint, subscribeIdempotencyTelemetry,
} from "@idempotencykit/core";
import {
  SqliteTransactionalHttpModule, SqliteTransactionalHttpService,
} from "@idempotencykit/nestjs-sqlite";

import { IdempotencyKitModule, Idempotent } from "@idempotencykit/nestjs";

const namespace = "sqlite-nest-http-test";
const createOperation = "orders.sqlite.nest.create.v1";
const folder = mkdtempSync(join(tmpdir(), "sqlite-nest-http-"));
const file = join(folder, "orders.db");
let app, base;
let createCalls = 0, failCalls = 0, standardCalls = 0;
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

function open() { return new DatabaseSync(file, { timeout: 100 }); }
function count(key) {
  const db = open();
  try {
    return Number(db.prepare(
      "SELECT COUNT(*) AS count FROM orders_sqlite_nest_http_test " +
      "WHERE client_key=?",
    ).get(key).count);
  } finally { db.close(); }
}
function countClaims(key) {
  const db = open();
  try {
    return Number(db.prepare(
      "SELECT COUNT(*) AS count FROM idempotencykit_records " +
      "WHERE scope_key_hash=?",
    ).get(key).count);
  } finally { db.close(); }
}

class TestAuthGuard {
  canActivate(context) {
    const req = context.switchToHttp().getRequest();
    if (!req.headers["x-test-user"]) return false;
    // TEST ONLY. Real applications must validate JWT/session credentials.
    req.user = {
      id: req.headers["x-test-user"],
      tenantId: req.headers["x-test-tenant"] ?? "acme",
    };
    return true;
  }
}
Injectable()(TestAuthGuard);

class SqliteHttpController {
  constructor(service) { this.service = service; }

  async create(req, res) {
    return this.service.handleExpress(
      req, res, createOperation,
      async (session, bodyJson, request) => {
        createCalls++;
        const dto = JSON.parse(bodyJson ?? "{}");
        const inserted = session.prepare(
          "INSERT INTO orders_sqlite_nest_http_test " +
          "(client_key,tenant_id,user_id,quantity) VALUES (?,?,?,?)",
        ).run(request.headers["idempotency-key"], request.user.tenantId,
          request.user.id, dto.quantity);
        const id = Number(inserted.lastInsertRowid);
        return {
          statusCode: 201,
          contentType: "application/json",
          body: Buffer.from(JSON.stringify({
            id, quantity: dto.quantity, itemId: dto.itemId,
          })),
          headers: { Location: "/orders/transactional/" + id },
        };
      },
      request => request.user.tenantId !== "revoked");
  }

  async fail(req, res) {
    return this.service.handleExpress(
      req, res, "orders.sqlite.nest.fail.v1",
      async (session, json, request) => {
        failCalls++;
        session.prepare(
          "INSERT INTO orders_sqlite_nest_http_test " +
          "(client_key,tenant_id,user_id,quantity) VALUES (?,?,?,1)",
        ).run(request.headers["idempotency-key"], request.user.tenantId,
          request.user.id);
        throw new Error("Injected SQLite HTTP domain failure.");
      });
  }
}
Inject(SqliteTransactionalHttpService)(SqliteHttpController, undefined, 0);
Controller("orders/transactional")(SqliteHttpController);
UseGuards(TestAuthGuard)(SqliteHttpController);
for (const [method, route] of [["create", ""], ["fail", "fail"]]) {
  const descriptor = Object.getOwnPropertyDescriptor(
    SqliteHttpController.prototype, method);
  Post(route)(SqliteHttpController.prototype, method, descriptor);
  Req()(SqliteHttpController.prototype, method, 0);
  Res()(SqliteHttpController.prototype, method, 1);
}

class StandardTelemetryController {
  write() { return { called: ++standardCalls }; }
  fail() { throw new Error("Domain failure: must not be part of telemetry."); }
}
Controller("standard-telemetry")(StandardTelemetryController);
UseGuards(TestAuthGuard)(StandardTelemetryController);
for (const method of ["write", "fail"]) {
  const descriptor = Object.getOwnPropertyDescriptor(StandardTelemetryController.prototype, method);
  Post(method)(StandardTelemetryController.prototype, method, descriptor);
  Idempotent("standard.telemetry." + method)(StandardTelemetryController.prototype, method, descriptor);
}

before(async () => {
  initializeSqliteDatabase(file);
  const db = open();
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS orders_sqlite_nest_http_test (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        client_key TEXT NOT NULL,
        tenant_id TEXT NOT NULL,
        user_id TEXT NOT NULL,
        quantity INTEGER NOT NULL
      );`);
  } finally { db.close(); }

  class AppModule {}
  Module({
    imports: [SqliteTransactionalHttpModule.forRoot({
      namespace,
      executor: new SqliteTransactionalExecutor(file, 70),
      retentionMs: 86_400_000,
      resolveCaller: req => ({
        principalId: req.user.id, tenantId: req.user.tenantId,
      }),
    }), IdempotencyKitModule.forRoot({
      namespace,
      store: new SqliteIdempotencyStore(file, 70),
      resolveCaller: req => ({ principalId: req.user.id, tenantId: req.user.tenantId }),
    })],
    controllers: [SqliteHttpController, StandardTelemetryController],
  })(AppModule);
  app = await NestFactory.create(AppModule, { rawBody: true, logger: false });
  await app.listen(0, "127.0.0.1");
  base = "http://127.0.0.1:" + app.getHttpServer().address().port;
});

after(async () => {
  if (app) await app.close();
  rmSync(folder, { recursive: true, force: true });
});

async function post(path, body, key, user = "alice", tenant = "acme", origin = base) {
  const headers = { "Content-Type": "application/json", "X-Test-Tenant": tenant };
  if (key !== null) headers["Idempotency-Key"] = key;
  if (user !== null) headers["X-Test-User"] = user;
  const response = await fetch(origin + path, {
    method: "POST", headers,
    body: typeof body === "string" ? body : JSON.stringify(body),
    signal: AbortSignal.timeout(12_000),
  });
  const raw = await response.text();
  return {
    status: response.status, raw,
    body: raw ? JSON.parse(raw) : null,
    location: response.headers.get("location"),
    retryAfter: response.headers.get("retry-after"),
  };
}
const path = "/orders/transactional";

test("real NestJS SQLite HTTP stores and replays identical bytes after reopen", async () => {
  const key = "sqlite-http-" + randomUUID();
  const first = await post(path, { itemId: 50, quantity: 2 }, key);
  assert.equal(first.status, 201);
  assert.equal(first.location, "/orders/transactional/" + first.body.id);
  const before = createCalls;
  const replay = await post(path, '{"quantity":2,"itemId":50}', key);
  assert.equal(replay.status, 201);
  assert.equal(replay.raw, first.raw);
  assert.equal(replay.location, first.location);
  assert.equal(createCalls, before);
  assert.equal(count(key), 1);
});

test("telemetry reports only bounded transaction outcomes, even with failing collectors", async () => {
  const events = [];
  const stopThrowing = subscribeIdempotencyTelemetry(() => {
    throw new Error("collector failure");
  });
  const stopRejecting = subscribeIdempotencyTelemetry(async () => {
    throw new Error("collector rejection");
  });
  const stop = subscribeIdempotencyTelemetry(event => { events.push(event); });
  const key = "private-telemetry-key-" + randomUUID();
  const body = { itemId: 920, quantity: 2 };
  try {
    const first = await post(path, body, key, "private-principal");
    assert.equal(first.status, 201);
    const replay = await post(path, body, key, "private-principal");
    assert.equal(replay.raw, first.raw);
    assert.equal((await post(path, { ...body, quantity: 3 }, key, "private-principal")).status, 422);
    assert.equal((await post(path, body, key, null)).status, 403);
    assert.equal((await post(path + "/fail", body, key, "private-principal")).status, 503);
    assert.equal(count(key), 1);
    assert.deepEqual(events.map(event => event.outcome),
      ["executed", "replayed", "fingerprint_mismatch", "error"]);
    for (const event of events) {
      assert.deepEqual(Object.keys(event).sort(), ["durationMs", "outcome", "stage"]);
      assert.equal(event.stage, "transactional.execute");
      assert.ok(Number.isFinite(event.durationMs) && event.durationMs >= 0);
    }
  } finally {
    stop();
    stopThrowing();
    stopRejecting();
  }
  assert.equal((await post(path, body, key, "private-principal")).status, 201);
  assert.equal(events.length, 4);
});

test("Standard HTTP telemetry separates claims from persistence and uncertain retries", async () => {
  const events = [];
  const stop = subscribeIdempotencyTelemetry(({ stage, outcome }) => { events.push([stage, outcome]); });
  const key = "private-telemetry-key-" + randomUUID();
  const body = { order: "private" };
  try {
    const first = await post("/standard-telemetry/write", body, key);
    assert.equal(first.status, 201);
    const replay = await post("/standard-telemetry/write", body, key);
    assert.equal(replay.status, 201);
    assert.deepEqual(replay.body, first.body);
    assert.equal(standardCalls, 1);
    assert.equal((await post("/standard-telemetry/fail", body, key)).status, 503);
    assert.equal((await post("/standard-telemetry/fail", body, key)).status, 409);
    assert.deepEqual(events, [
      ["standard.claim", "acquired"],
      ["standard.execute", "completed"],
      ["standard.claim", "replayed"],
      ["standard.claim", "acquired"],
      ["standard.execute", "unconfirmed"],
      ["standard.claim", "uncertain"],
    ]);
  } finally { stop(); }
});

test("HTTP missing key, malformed JSON and different fingerprint reject before callback", async () => {
  const key = "sqlite-http-" + randomUUID();
  const first = await post(path, { itemId: 11, quantity: 3 }, key);
  assert.equal(first.status, 201);
  const previous = createCalls;
  const mismatch = await post(path, { itemId: 11, quantity: 4 }, key);
  assert.equal(mismatch.status, 422);
  assert.equal(mismatch.body.code, "IDEMPOTENCY_FINGERPRINT_MISMATCH");
  assert.equal((await post(path, { itemId: 11, quantity: 3 }, null)).status, 400);
  assert.equal((await post(path, '{"quantity":}', "sqlite-http-" + randomUUID())).status, 400);
  assert.equal(createCalls, previous);
});

test("authenticated principal and tenant are trusted server-derived scope", async () => {
  const key = "sqlite-http-" + randomUUID(), body = { itemId: 50, quantity: 4 };
  assert.equal((await post(path, body, key, "alice", "acme")).status, 201);
  assert.equal((await post(path, body, key, "bob", "acme")).status, 201);
  assert.equal((await post(path, body, key, "alice", "other")).status, 201);
  assert.equal(count(key), 3);
});

test("fresh resource authorization blocks replay, and guard blocks unauthenticated", async () => {
  const key = "sqlite-http-" + randomUUID(), body = { itemId: 50, quantity: 6 };
  assert.equal((await post(path, body, key)).status, 201);
  const before = createCalls;
  const denied = await post(path, body, key, "alice", "revoked");
  assert.equal(denied.status, 403);
  assert.equal(denied.body.code, "IDEMPOTENCY_OPERATION_FORBIDDEN");
  const missingUser = await post(path, body, key, null);
  assert.equal(missingUser.status, 403); // Test guard runs before HTTP service.
  assert.equal(createCalls, before);
});

test("genuine committed matching processing record -> HTTP 409; uncertain never executes", async () => {
  const key = "sqlite-http-" + randomUUID(), body = { itemId: 50, quantity: 8 };
  const scope = computeScopeKey({
    namespace, operationId: createOperation,
    principalId: "alice", tenantId: "acme", clientKey: key,
  });
  const fingerprint = computeRequestFingerprint({
    method: "POST", operationId: createOperation, path,
    bodyJson: JSON.stringify(body),
  });
  const store = new SqliteIdempotencyStore(file);
  const claim = await store.tryClaim(scope, fingerprint, 60_000);
  assert.equal(claim.status, "acquired");
  const previous = createCalls;
  const running = await post(path, body, key);
  assert.equal(running.status, 409);
  assert.equal(running.retryAfter, "1");
  assert.equal(running.body.code, "IDEMPOTENCY_IN_PROGRESS");
  assert.equal(await store.tryMarkUncertain(scope, fingerprint, claim.ownerToken), true);
  const uncertain = await post(path, body, key);
  assert.equal(uncertain.status, 409);
  assert.equal(uncertain.body.code, "IDEMPOTENCY_OUTCOME_UNCERTAIN");
  assert.equal(createCalls, previous);
});

test("failed SQLite domain INSERT rolls back claim and allows same-key reattempt", async () => {
  const key = "sqlite-http-" + randomUUID();
  const previous = failCalls;
  for (let i = 0; i < 2; i++) {
    const failed = await post(path + "/fail", { quantity: 1 }, key);
    assert.equal(failed.status, 503);
    assert.equal(failed.body.code, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
    assert.equal(count(key), 0);
  }
  assert.equal(failCalls, previous + 2);
});

test("16 concurrent authenticated HTTP retries commit only one SQLite domain row", async () => {
  const key = "sqlite-http-" + randomUUID();
  const previous = createCalls;
  const calls = await Promise.all(Array.from({ length: 16 },
    () => post(path, { itemId: 99, quantity: 4 }, key)));
  assert.ok(calls.every(x => [201, 503].includes(x.status)));
  assert.equal(createCalls, previous + 1);
  assert.equal(count(key), 1);
});

function startCrashWorker(mode, key) {
  const worker = fork(
    fileURLToPath(new URL("./support/sqlite-transactional-http-crash-worker.mjs",
      import.meta.url)),
    [mode, file, key], {
      env: process.env,
      stdio: ["ignore", "ignore", "pipe", "ipc"],
    },
  );
  let stderr = "";
  worker.stderr?.on("data", data => { stderr += String(data); });
  return { worker, stderr: () => stderr };
}

function waitPhase(worker, phase, stderr) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error("SQLite HTTP worker timed out: " + phase + " " + stderr()));
    }, 15_000);
    const message = data => {
      if (data?.phase !== phase) return;
      cleanup(); resolve(data);
    };
    const terminated = (code, signal) => {
      cleanup();
      reject(new Error("Worker terminated before " + phase + ": " +
        code + "/" + signal + " " + stderr()));
    };
    const failed = error => { cleanup(); reject(error); };
    const cleanup = () => {
      clearTimeout(timeout);
      worker.off("message", message);
      worker.off("exit", terminated);
      worker.off("error", failed);
    };
    worker.on("message", message);
    worker.on("exit", terminated);
    worker.on("error", failed);
  });
}
async function killWorker(worker) {
  if (worker.exitCode !== null || worker.signalCode !== null) return;
  const exited = new Promise(resolve => worker.once("exit", resolve));
  assert.equal(worker.kill("SIGKILL"), true);
  await exited;
}
async function workerRequest(port, key) {
  try {
    return await post(path, { itemId: 50, quantity: 7 }, key,
      "alice", "acme", "http://127.0.0.1:" + port);
  } catch (error) {
    return error;
  }
}

test("unrelated foreign SQLite writer -> HTTP 503, not false 409", async () => {
  const key = "sqlite-http-" + randomUUID();
  const { worker, stderr } = startCrashWorker("unrelated-writer", key);
  try {
    await waitPhase(worker, "locked", stderr);
    const before = createCalls;
    const busy = await post(path, { itemId: 50, quantity: 7 }, key);
    assert.equal(busy.status, 503);
    assert.equal(busy.body.code, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
    assert.equal(createCalls, before);
  } finally {
    await killWorker(worker);
  }
  const retried = await post(path, { itemId: 50, quantity: 7 }, key);
  assert.equal(retried.status, 201);
  assert.equal(count(key), 1);
});

test("SIGKILL live NestJS SQLite HTTP process BEFORE COMMIT -> safe same-key execution", async () => {
  const key = "sqlite-http-" + randomUUID();
  const { worker, stderr } = startCrashWorker("before-commit", key);
  try {
    const ready = waitPhase(worker, "ready", stderr);
    const { port } = await ready;
    const checkpoint = waitPhase(worker, "before-commit", stderr);
    const pending = workerRequest(port, key);
    await checkpoint;
    await killWorker(worker);
    assert.ok((await pending) instanceof Error);
  } finally {
    await killWorker(worker);
  }
  assert.equal(count(key), 0);
  const before = createCalls;
  const retry = await post(path, { itemId: 50, quantity: 7 }, key);
  assert.equal(retry.status, 201);
  assert.equal(createCalls, before + 1);
  assert.equal(count(key), 1);
});

test("SIGKILL live NestJS SQLite HTTP process AFTER COMMIT but BEFORE reply -> replay", async () => {
  const key = "sqlite-http-" + randomUUID();
  const { worker, stderr } = startCrashWorker("after-commit", key);
  try {
    const ready = waitPhase(worker, "ready", stderr);
    const { port } = await ready;
    const checkpoint = waitPhase(worker, "after-commit", stderr);
    const pending = workerRequest(port, key);
    await checkpoint;
    assert.equal(count(key), 1);
    await killWorker(worker);
    assert.ok((await pending) instanceof Error);
  } finally {
    await killWorker(worker);
  }
  const before = createCalls;
  const retry = await post(path, { itemId: 50, quantity: 7 }, key);
  assert.equal(retry.status, 201);
  assert.equal(retry.body.quantity, 7);
  assert.equal(createCalls, before);
  assert.equal(count(key), 1);
});
