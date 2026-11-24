import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { fork } from "node:child_process";
import { fileURLToPath } from "node:url";
import { readFileSync } from "node:fs";
import "reflect-metadata";
import { Controller, Post, Module, Injectable, Inject, Req, Res, UseGuards } from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import sql from "mssql";
import {
  SqlServerTransactionalHttpModule, SqlServerTransactionalHttpService,
} from "@idempotencykit/nestjs-sqlserver";
import { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";

const password = process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PASSWORD;
const integration = (name, callback) => test(name, { skip: !password }, callback);
let pool, app, base;
let createCalls = 0, failCalls = 0, slowCalls = 0;

const config = {
  server: process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_HOST ?? "127.0.0.1",
  port: Number(process.env.IDEMPOTENCYKIT_TEST_SQLSERVER_PORT ?? "1433"),
  database: "master",
  user: "sa", password: password ?? "",
  options: { encrypt: true, trustServerCertificate: true },
  pool: { max: 36, min: 0, idleTimeoutMillis: 30_000 },
  connectionTimeout: 5_000, requestTimeout: 30_000,
};
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

class TestAuthGuard {
  canActivate(context) {
    const req = context.switchToHttp().getRequest();
    if (!req.headers["x-test-user"]) return false;
    // Only a test authentication guard; real applications verify JWT/cookies.
    req.user = {
      id: req.headers["x-test-user"],
      tenantId: req.headers["x-test-tenant"] ?? "acme",
    };
    return true;
  }
}
Injectable()(TestAuthGuard);

class SqlServerHttpController {
  constructor(service) { this.service = service; }

  async create(req, res) {
    return this.service.handleExpress(
      req, res, "orders.sqlserver.nest.create.v1",
      async (transaction, bodyJson, request) => {
        createCalls++;
        const parsed = JSON.parse(bodyJson ?? "{}");
        const result = await new sql.Request(transaction)
          .input("key", sql.NVarChar(128), request.headers["idempotency-key"])
          .input("tenant", sql.NVarChar(100), request.user.tenantId)
          .input("principal", sql.NVarChar(100), request.user.id)
          .input("qty", sql.Int, parsed.quantity)
          .query(`
            INSERT INTO dbo.idempotencykit_sqlserver_nest_http_orders_test
                (client_key, tenant_id, user_id, quantity)
            OUTPUT INSERTED.id VALUES (@key, @tenant, @principal, @qty)`);
        const id = Number(result.recordset[0].id);
        return {
          statusCode: 201, contentType: "application/json",
          body: Buffer.from(JSON.stringify({ id, quantity: parsed.quantity,
            itemId: parsed.itemId })),
          headers: { Location: "/orders/transactional/" + id },
        };
      },
      request => request.user.tenantId !== "revoked");
  }

  async slow(req, res) {
    return this.service.handleExpress(
      req, res, "orders.sqlserver.nest.slow.v1",
      async (transaction, bodyJson, request) => {
        slowCalls++;
        await wait(270);
        const dto = JSON.parse(bodyJson ?? "{}");
        const result = await new sql.Request(transaction)
          .input("key", sql.NVarChar(128), request.headers["idempotency-key"])
          .input("tenant", sql.NVarChar(100), request.user.tenantId)
          .input("principal", sql.NVarChar(100), request.user.id)
          .input("qty", sql.Int, dto.quantity)
          .query(`
            INSERT INTO dbo.idempotencykit_sqlserver_nest_http_orders_test
                (client_key, tenant_id, user_id, quantity)
            OUTPUT INSERTED.id VALUES (@key, @tenant, @principal, @qty)`);
        return {
          statusCode: 201, contentType: "application/json",
          body: Buffer.from(JSON.stringify({ id: Number(result.recordset[0].id) })),
          headers: {},
        };
      });
  }

  async fail(req, res) {
    return this.service.handleExpress(
      req, res, "orders.sqlserver.nest.fail.v1",
      async (transaction, bodyJson, request) => {
        failCalls++;
        await new sql.Request(transaction)
          .input("key", sql.NVarChar(128), request.headers["idempotency-key"])
          .input("tenant", sql.NVarChar(100), request.user.tenantId)
          .input("principal", sql.NVarChar(100), request.user.id)
          .query(`
            INSERT INTO dbo.idempotencykit_sqlserver_nest_http_orders_test
                (client_key, tenant_id, user_id, quantity)
            VALUES (@key, @tenant, @principal, 1)`);
        throw new Error("Simulated SQL Server business handler failure.");
      });
  }
}
Inject(SqlServerTransactionalHttpService)(SqlServerHttpController, undefined, 0);
Controller("orders/transactional")(SqlServerHttpController);
UseGuards(TestAuthGuard)(SqlServerHttpController);
for (const [method, route] of [["create", ""], ["slow", "slow"], ["fail", "fail"]]) {
  const descriptor = Object.getOwnPropertyDescriptor(SqlServerHttpController.prototype, method);
  Post(route)(SqlServerHttpController.prototype, method, descriptor);
  Req()(SqlServerHttpController.prototype, method, 0);
  Res()(SqlServerHttpController.prototype, method, 1);
}

before(async () => {
  if (!password) return;
  let last;
  for (let i = 0; i < 100; i++) {
    const attempt = new sql.ConnectionPool(config);
    try {
      pool = await attempt.connect();
      break;
    } catch (error) {
      last = error;
      try { await attempt.close(); } catch {}
      await wait(1000);
    }
  }
  if (!pool) throw new Error("SQL Server unavailable: " + last?.message);

  await pool.request().batch(readFileSync(new URL(
    "../../spec/sql/sqlserver/001_idempotencykit_records.sql", import.meta.url), "utf8"));
  await pool.request().batch(`
    IF OBJECT_ID(N'dbo.idempotencykit_sqlserver_nest_http_orders_test', N'U') IS NULL
      EXEC(N'CREATE TABLE dbo.idempotencykit_sqlserver_nest_http_orders_test
        (id bigint IDENTITY(1,1) PRIMARY KEY,
         client_key nvarchar(128) NOT NULL,
         tenant_id nvarchar(100) NOT NULL,
         user_id nvarchar(100) NOT NULL,
         quantity int NOT NULL)')`);

  class TestModule {}
  Module({
    imports: [SqlServerTransactionalHttpModule.forRoot({
      namespace: "sqlserver-nest-http-test",
      executor: new SqlServerTransactionalExecutor(pool, 50),
      retentionMs: 180_000,
      resolveCaller: req => ({
        principalId: req.user.id, tenantId: req.user.tenantId,
      }),
    })],
    controllers: [SqlServerHttpController],
  })(TestModule);
  app = await NestFactory.create(TestModule, { rawBody: true, logger: false });
  await app.listen(0, "127.0.0.1");
  base = "http://127.0.0.1:" + app.getHttpServer().address().port;
});
after(async () => {
  if (app) await app.close();
  if (pool) await pool.close();
});

async function count(key) {
  const result = await pool.request()
    .input("key", sql.NVarChar(128), key)
    .query(`
      SELECT COUNT_BIG(*) AS total
      FROM dbo.idempotencykit_sqlserver_nest_http_orders_test
      WHERE client_key = @key`);
  return Number(result.recordset[0].total);
}

async function post(path, body, key, user = "alice", tenant = "acme") {
  const headers = { "Content-Type": "application/json", "X-Test-Tenant": tenant };
  if (key !== null) headers["Idempotency-Key"] = key;
  if (user !== null) headers["X-Test-User"] = user;
  const result = await fetch(base + path, {
    method: "POST", headers, body: typeof body === "string" ? body : JSON.stringify(body),
  });
  const raw = await result.text();
  return {
    status: result.status, raw,
    body: raw ? JSON.parse(raw) : null,
    location: result.headers.get("location"),
    retryAfter: result.headers.get("retry-after"),
  };
}

integration("NestJS SQL Server HTTP commits then exactly replays stored bytes", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const first = await post("/orders/transactional", { itemId: 50, quantity: 2 }, key);
  assert.equal(first.status, 201);
  assert.equal(first.location, "/orders/transactional/" + first.body.id);
  const before = createCalls;
  const replay = await post("/orders/transactional",
    '{"quantity":2,"itemId":50}', key);
  assert.equal(replay.status, 201);
  assert.equal(replay.raw, first.raw);
  assert.equal(replay.location, first.location);
  assert.equal(createCalls, before);
  assert.equal(await count(key), 1);
});

integration("missing key is 400, mismatched body is 422; callback not rerun", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const first = await post("/orders/transactional", { itemId: 12, quantity: 4 }, key);
  assert.equal(first.status, 201);
  const before = createCalls;
  const mismatch = await post("/orders/transactional", { itemId: 12, quantity: 3 }, key);
  assert.equal(mismatch.status, 422);
  assert.equal(mismatch.body.code, "IDEMPOTENCY_FINGERPRINT_MISMATCH");
  const missing = await post("/orders/transactional", { itemId: 12, quantity: 4 }, null);
  assert.equal(missing.status, 400);
  assert.equal(createCalls, before);
});

integration("fresh authorization blocks replay and auth guard blocks unauthenticated caller", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const first = await post("/orders/transactional", { itemId: 12, quantity: 4 }, key);
  assert.equal(first.status, 201);
  const before = createCalls;
  const denied = await post("/orders/transactional", { itemId: 12, quantity: 4 }, key, "alice", "revoked");
  assert.equal(denied.status, 403);
  assert.equal(denied.body.code, "IDEMPOTENCY_OPERATION_FORBIDDEN");
  const unauthorized = await post("/orders/transactional", { itemId: 12, quantity: 4 }, key, null);
  assert.equal(unauthorized.status, 403); // Test guard runs before the service.
  assert.equal(createCalls, before);
});

integration("trusted caller and tenant scopes isolate the same raw key", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const body = { itemId: 12, quantity: 5 };
  assert.equal((await post("/orders/transactional", body, key, "alice", "tenant-a")).status, 201);
  assert.equal((await post("/orders/transactional", body, key, "bob", "tenant-a")).status, 201);
  assert.equal((await post("/orders/transactional", body, key, "alice", "tenant-b")).status, 201);
  assert.equal(await count(key), 3);
});

integration("same-key live lock produces 409 retry-after then completes and replays", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const before = slowCalls;
  const first = post("/orders/transactional/slow", { quantity: 8 }, key);
  for (let i = 0; i < 100 && slowCalls === before; i++)
    await wait(5);
  assert.equal(slowCalls, before + 1);
  const during = await post("/orders/transactional/slow", { quantity: 8 }, key);
  assert.equal(during.status, 409);
  assert.equal(during.retryAfter, "1");
  assert.equal(during.body.code, "IDEMPOTENCY_IN_PROGRESS");
  assert.equal((await first).status, 201);
  const replay = await post("/orders/transactional/slow", { quantity: 8 }, key);
  assert.equal(replay.status, 201);
  assert.equal(slowCalls, before + 1);
  assert.equal(await count(key), 1);
});

integration("failed business callback rolls back both SQL rows; same-key retry can execute", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const before = failCalls;
  const first = await post("/orders/transactional/fail", { quantity: 1 }, key);
  assert.equal(first.status, 503);
  assert.equal(first.body.code, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
  assert.equal(await count(key), 0);
  const second = await post("/orders/transactional/fail", { quantity: 1 }, key);
  assert.equal(second.status, 503);
  assert.equal(failCalls, before + 2);
  assert.equal(await count(key), 0);
});

integration("12 concurrent HTTP retries commit one SQL Server domain row", async () => {
  const key = "sqlserver-http-" + randomUUID();
  const before = createCalls;
  const results = await Promise.all(Array.from({ length: 12 }, () =>
    post("/orders/transactional", { itemId: 99, quantity: 2 }, key)));
  assert.ok(results.every(result => [201, 409].includes(result.status)));
  assert.equal(createCalls, before + 1);
  assert.equal(await count(key), 1);
});

function startCrashWorker(mode) {
  const worker = fork(
    fileURLToPath(new URL("./support/sqlserver-transactional-http-crash-worker.mjs",
      import.meta.url)),
    [mode], { env: process.env, stdio: ["ignore", "ignore", "pipe", "ipc"] },
  );
  let stderr = "";
  worker.stderr?.on("data", data => { stderr += String(data); });
  return { worker, stderr: () => stderr };
}

function waitForPhase(worker, phase, stderr) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      clearListeners();
      reject(new Error("Crash worker timed out at " + phase + ": " + stderr()));
    }, 15_000);
    const onMessage = msg => {
      if (msg?.phase !== phase) return;
      clearListeners();
      resolve(msg);
    };
    const onExit = (code, signal) => {
      clearListeners();
      reject(new Error("Worker exited before " + phase + ": " +
        code + "/" + signal + " " + stderr()));
    };
    const onError = error => {
      clearListeners();
      reject(error);
    };
    const clearListeners = () => {
      clearTimeout(timeout);
      worker.off("message", onMessage);
      worker.off("exit", onExit);
      worker.off("error", onError);
    };
    worker.on("message", onMessage);
    worker.on("exit", onExit);
    worker.on("error", onError);
  });
}

async function killWorker(worker) {
  if (worker.exitCode !== null || worker.signalCode !== null) return;
  const exited = new Promise(resolve => worker.once("exit", resolve));
  if (!worker.kill("SIGKILL"))
    throw new Error("Could not kill SQL Server test worker.");
  await exited;
}

async function requestWorker(port, key) {
  const response = await fetch(
    "http://127.0.0.1:" + port + "/orders/transactional",
    {
      method: "POST",
      headers: {
        "Idempotency-Key": key,
        "Content-Type": "application/json",
        "X-Test-User": "alice", "X-Test-Tenant": "acme",
      },
      body: JSON.stringify({ itemId: 50, quantity: 7 }),
      signal: AbortSignal.timeout(12_000),
    },
  );
  return { status: response.status, raw: await response.text() };
}

integration("live SQL Server HTTP SIGKILL BEFORE COMMIT rolls back and same-key retry executes", async () => {
  const key = "sqlserver-crash-" + randomUUID();
  const { worker, stderr } = startCrashWorker("before-commit");
  const ready = waitForPhase(worker, "ready", stderr);
  try {
    const { port } = await ready;
    const checkpoint = waitForPhase(worker, "before-commit", stderr);
    const pending = requestWorker(port, key).then(value => value, error => error);
    await checkpoint;
    // The worker has an uncommitted SQL INSERT and claim. Killing the entire
    // HTTP server closes that SQL connection and SQL Server rolls both back.
    await killWorker(worker);
    assert.ok((await pending) instanceof Error);
  } finally {
    await killWorker(worker);
  }
  assert.equal(await count(key), 0);
  const before = createCalls;
  const retry = await post("/orders/transactional", { itemId: 50, quantity: 7 }, key);
  assert.equal(retry.status, 201);
  assert.equal(await count(key), 1);
  assert.equal(createCalls, before + 1);
});

integration("live SQL Server HTTP SIGKILL AFTER COMMIT replays without a second SQL INSERT", async () => {
  const key = "sqlserver-crash-" + randomUUID();
  const { worker, stderr } = startCrashWorker("after-commit");
  const ready = waitForPhase(worker, "ready", stderr);
  try {
    const { port } = await ready;
    const checkpoint = waitForPhase(worker, "after-commit", stderr);
    const pending = requestWorker(port, key).then(value => value, error => error);
    await checkpoint;
    assert.equal(await count(key), 1);
    await killWorker(worker);
    assert.ok((await pending) instanceof Error);
  } finally {
    await killWorker(worker);
  }
  const before = createCalls;
  const replay = await post("/orders/transactional", { itemId: 50, quantity: 7 }, key);
  assert.equal(replay.status, 201);
  assert.equal(replay.body.quantity, 7);
  assert.equal(await count(key), 1);
  assert.equal(createCalls, before);
});

