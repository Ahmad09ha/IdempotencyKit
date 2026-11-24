import "reflect-metadata";
import test from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdtempSync, rmSync, chmodSync, statSync, readdirSync, openSync, closeSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { Controller, Post, Module, Inject, Injectable, Req, Res, UseGuards, UnauthorizedException } from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { initializeSqliteDatabase, SqliteTransactionalExecutor } from "@idempotencykit/sqlite";
import { SqliteTransactionalHttpModule, SqliteTransactionalHttpService } from "@idempotencykit/nestjs-sqlite";

const supported = process.platform === "linux" && process.getuid() !== 0;
test("actual filesystem permission denial fails closed; same-key replay and retry recover", {
  skip: supported ? false : "Requires an unprivileged Linux user", timeout: 30000,
}, async () => {
  const folder = mkdtempSync(join(tmpdir(), "idk-permission-fault-"));
  const file = join(folder, "orders.db");
  let app, callbacks = 0;
  function counts() {
    const db = new DatabaseSync(file, { readOnly: true });
    try {
      const orders = db.prepare("SELECT COUNT(*) AS n FROM orders").get().n;
      const total = db.prepare("SELECT COUNT(*) AS n FROM idempotencykit_records").get().n;
      const completed = db.prepare("SELECT COUNT(*) AS n FROM idempotencykit_records WHERE state='completed'").get().n;
      assert.equal(total, completed);
      return [orders, completed];
    } finally { db.close(); }
  }
  function denied(path, flags) {
    assert.throws(() => { const fd = openSync(path, flags); closeSync(fd); },
      error => error.code === "EACCES");
  }
  try {
    initializeSqliteDatabase(file);
    const db = new DatabaseSync(file);
    try { db.exec("CREATE TABLE orders (id TEXT PRIMARY KEY, quantity INTEGER NOT NULL)"); }
    finally { db.close(); }
    class FixtureAuth {
      canActivate(context) {
        const req = context.switchToHttp().getRequest();
        if (req.headers["x-test-user"] !== "fixture-user") throw new UnauthorizedException();
        req.user = { id: "fixture-user", tenantId: "fixture-tenant" };
        return true;
      }
    }
    Injectable()(FixtureAuth);
    class Orders {
      constructor(service) { this.service = service; }
      create(req, res) {
        return this.service.handleExpress(req, res, "create-order", async session => {
          callbacks++;
          const id = randomUUID().replaceAll("-", "");
          session.prepare("INSERT INTO orders VALUES (?, 2)").run(id);
          return { statusCode: 201, contentType: "application/json",
            body: Buffer.from(JSON.stringify({ id, quantity: 2 })),
            headers: { Location: "/orders/" + id } };
        });
      }
    }
    Inject(SqliteTransactionalHttpService)(Orders, undefined, 0);
    Controller("orders")(Orders);
    UseGuards(FixtureAuth)(Orders);
    Post()(Orders.prototype, "create", Object.getOwnPropertyDescriptor(Orders.prototype, "create"));
    Req()(Orders.prototype, "create", 0);
    Res()(Orders.prototype, "create", 1);
    class FixtureModule {}
    Module({ imports: [SqliteTransactionalHttpModule.forRoot({
      namespace: "sqlite-permission-lab", executor: new SqliteTransactionalExecutor(file, 100),
      retentionMs: 3600000,
      resolveCaller: req => ({ principalId: req.user.id, tenantId: req.user.tenantId }),
    })], controllers: [Orders] })(FixtureModule);
    app = await NestFactory.create(FixtureModule, { rawBody: true, logger: false });
    await app.listen(0, "127.0.0.1");
    const url = "http://127.0.0.1:" + app.getHttpServer().address().port + "/orders";
    async function request(key) {
      const response = await fetch(url, { method: "POST", signal: AbortSignal.timeout(5000),
        headers: { "content-type": "application/json", "idempotency-key": key, "x-test-user": "fixture-user" },
        body: '{"quantity":2}' });
      return { status: response.status, location: response.headers.get("location"),
        bytes: Buffer.from(await response.arrayBuffer()) };
    }
    function assertUnavailable(response) {
      assert.equal(response.status, 503, response.bytes.toString());
      assert.equal(JSON.parse(response.bytes).code, "IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
    }
    function assertCreated(response) {
      assert.equal(response.status, 201, response.bytes.toString());
      const result = JSON.parse(response.bytes);
      assert.match(result.id, /^[a-f0-9]{32}$/);
      assert.equal(result.quantity, 2);
      assert.equal(response.location, "/orders/" + result.id);
    }
    const original = await request("already-completed");
    assertCreated(original);
    assert.deepEqual(counts(), [1, 1]);

    const dirMode = statSync(folder).mode & 0o777;
    const modes = readdirSync(folder).map(name => {
      const path = join(folder, name); return [path, statSync(path).mode & 0o777];
    });
    try {
      for (const [path] of modes) chmodSync(path, 0o400);
      chmodSync(folder, 0o500);
      // Verify the test account really cannot write rather than relying on chmod alone.
      denied(file, "r+");
      denied(join(folder, "write-probe"), "wx");
      assertUnavailable(await request("new-operation"));
      assertUnavailable(await request("already-completed"));
      assert.equal(callbacks, 1);
    } finally {
      chmodSync(folder, dirMode);
      for (const [path, mode] of modes) chmodSync(path, mode);
    }
    assert.deepEqual(counts(), [1, 1]);
    assert.deepEqual(await request("already-completed"), original);
    assert.equal(callbacks, 1);
    const recovered = await request("new-operation");
    assertCreated(recovered);
    assert.deepEqual(await request("new-operation"), recovered);
    assert.deepEqual(counts(), [2, 2]);
    assert.equal(callbacks, 2);
  } finally {
    try { await app?.close(); }
    finally { rmSync(folder, { recursive: true, force: true }); }
  }
});
