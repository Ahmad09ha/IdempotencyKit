import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import "reflect-metadata";
import { Controller, Post, Get, Body, Header, Module, Injectable, UseGuards } from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import pg from "pg";
import { IdempotencyKitModule, Idempotent } from "@idempotencykit/nestjs";
import { PostgresIdempotencyStore } from "@idempotencykit/postgres";

const connectionString = process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL;
let pool, app, base;
let calls = 0, slowCalls = 0, failedCalls = 0;

class TestAuthGuard {
  canActivate(context) {
    const req = context.switchToHttp().getRequest();
    if (!req.headers["x-test-user"]) return false;
    // Test-only simulated authentication: never use client headers as production identity.
    req.user = {
      id: req.headers["x-test-user"],
      tenantId: req.headers["x-test-tenant"] ?? "tenant-default",
    };
    return true;
  }
}
Injectable()(TestAuthGuard);

class OrdersController {
  async create(dto) {
    calls++;
    return { id: calls, productId: dto.productId, quantity: dto.quantity };
  }

  async slow(dto) {
    slowCalls++;
    await new Promise(resolve => setTimeout(resolve, 220));
    return { id: slowCalls, quantity: dto.quantity };
  }

  async fail(_dto) {
    failedCalls++;
    throw new Error("Simulated handler error after a possible business effect");
  }

  health() { return { status: "ok" }; }
}

function decorate(method, decorators, withBody = true) {
  const descriptor = Object.getOwnPropertyDescriptor(OrdersController.prototype, method);
  for (const decorator of decorators) decorator(OrdersController.prototype, method, descriptor);
  if (withBody) Body()(OrdersController.prototype, method, 0);
}
Controller("orders")(OrdersController);
UseGuards(TestAuthGuard)(OrdersController);
decorate("create", [Post(), Idempotent("orders.create.v1"), Header("Location", "/orders/example")]);
decorate("slow", [Post("slow"), Idempotent("orders.slow.v1")]);
decorate("fail", [Post("fail"), Idempotent("orders.fail.v1")]);
decorate("health", [Get("health")], false);

const integration = (name, fn) => test(name, { skip: !connectionString }, fn);
before(async () => {
  if (!connectionString) return;
  pool = new pg.Pool({ connectionString });
  const sql = readFileSync(fileURLToPath(new URL(
    "../../spec/sql/postgres/001_idempotencykit_records.sql", import.meta.url
  )), "utf8");
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
  class TestModule {}
  Module({
    imports: [IdempotencyKitModule.forRoot({
      namespace: "idempotencykit-http-test",
      store: new PostgresIdempotencyStore(pool),
      resolveCaller: req => ({ principalId: req.user.id, tenantId: req.user.tenantId }),
      leaseMs: 10_000,
      retentionMs: 120_000,
    })],
    controllers: [OrdersController],
  })(TestModule);

  app = await NestFactory.create(TestModule, { rawBody: true, logger: false });
  await app.listen(0, "127.0.0.1");
  const address = app.getHttpServer().address();
  base = "http://127.0.0.1:" + address.port;
});
after(async () => {
  if (app) await app.close();
  if (pool) await pool.end();
});

async function request(path, body, key, user = "alice", tenant = "acme") {
  const headers = {
    "Content-Type": "application/json",
    "X-Test-User": user,
    "X-Test-Tenant": tenant,
  };
  if (key !== null) headers["Idempotency-Key"] = key;
  const response = await fetch(base + path, {
    method: "POST", headers, body: JSON.stringify(body),
  });
  const content = await response.text();
  return {
    status: response.status,
    json: content ? JSON.parse(content) : undefined,
    retryAfter: response.headers.get("retry-after"),
    location: response.headers.get("location"),
  };
}

integration("decorated route stores and replays original JSON", async () => {
  const key = "nest-" + randomUUID();
  const first = await request("/orders", { productId: 50, quantity: 2 }, key);
  assert.equal(first.status, 201);
  assert.equal(first.location, "/orders/example");
  const afterFirst = calls;
  const replay = await request("/orders", { quantity: 2, productId: 50 }, key);
  assert.equal(replay.status, 201);
  assert.deepEqual(replay.json, first.json);
  assert.equal(replay.location, first.location);
  assert.equal(calls, afterFirst);
});

integration("changed body on same key gets 422", async () => {
  const key = "nest-" + randomUUID();
  assert.equal((await request("/orders", { quantity: 2 }, key)).status, 201);
  const before = calls;
  const response = await request("/orders", { quantity: 3 }, key);
  assert.equal(response.status, 422);
  assert.equal(response.json.code, "IDEMPOTENCY_FINGERPRINT_MISMATCH");
  assert.equal(calls, before);
});

integration("missing key returns 400; unprotected GET works", async () => {
  const before = calls;
  assert.equal((await request("/orders", { quantity: 1 }, null)).status, 400);
  assert.equal(calls, before);
  const response = await fetch(base + "/orders/health", {
    headers: { "X-Test-User": "alice" },
  });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { status: "ok" });
});

integration("different authenticated principals and tenants are isolated", async () => {
  const key = "nest-" + randomUUID(), body = { productId: 9, quantity: 1 };
  const a = await request("/orders", body, key, "alice", "tenant-a");
  const b = await request("/orders", body, key, "bob", "tenant-a");
  const c = await request("/orders", body, key, "alice", "tenant-b");
  assert.equal(a.status, 201);
  assert.equal(b.status, 201);
  assert.equal(c.status, 201);
  assert.notEqual(a.json.id, b.json.id);
  assert.notEqual(a.json.id, c.json.id);
});

integration("in-progress duplicate gets 409 and Retry-After", async () => {
  const key = "nest-" + randomUUID(), before = slowCalls;
  const pending = request("/orders/slow", { quantity: 4 }, key);
  for (let i = 0; i < 100 && slowCalls === before; i++)
    await new Promise(resolve => setTimeout(resolve, 5));
  assert.equal(slowCalls, before + 1);
  const conflict = await request("/orders/slow", { quantity: 4 }, key);
  assert.equal(conflict.status, 409);
  assert.equal(conflict.retryAfter, "1");
  assert.equal(conflict.json.code, "IDEMPOTENCY_IN_PROGRESS");
  assert.equal((await pending).status, 201);
  assert.equal(slowCalls, before + 1);
});

integration("failed handler stays uncertain rather than reexecuting", async () => {
  const key = "nest-" + randomUUID(), before = failedCalls;
  const first = await request("/orders/fail", { productId: 5 }, key);
  assert.equal(first.status, 503);
  assert.equal(first.json.code, "IDEMPOTENCY_EXECUTION_UNCERTAIN");
  const second = await request("/orders/fail", { productId: 5 }, key);
  assert.equal(second.status, 409);
  assert.equal(second.json.code, "IDEMPOTENCY_OUTCOME_UNCERTAIN");
  assert.equal(failedCalls, before + 1);
});

integration("auth guard blocks unauthorized calls before interception", async () => {
  const response = await fetch(base + "/orders", {
    method: "POST",
    headers: { "Idempotency-Key": "nest-" + randomUUID(), "Content-Type": "application/json" },
    body: JSON.stringify({ productId: 1, quantity: 2 }),
  });
  assert.equal(response.status, 403);
});
