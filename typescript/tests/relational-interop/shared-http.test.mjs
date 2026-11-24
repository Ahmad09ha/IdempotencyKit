// Dedicated CI: both real HTTP frameworks against ONE disposable database.
// Synthetic authentication is test-only. Never point this lab at production.
import "reflect-metadata";
import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { setTimeout as sleep } from "node:timers/promises";
import { runHttpBaseline, readRssMiB } from "../support/http-performance-baseline.mjs";
import pg from "pg";
import sql from "mssql";
import {
  Controller, Post, Module, Inject, Injectable, Req, Res, UseGuards, UnauthorizedException,
} from "@nestjs/common";
import { NestFactory } from "@nestjs/core";
import { computeScopeKey, computeRequestFingerprint } from "@idempotencykit/core";
import { PostgresTransactionalExecutor } from "@idempotencykit/postgres";
import { SqlServerTransactionalExecutor } from "@idempotencykit/sqlserver";
import { PostgresTransactionalHttpModule, PostgresTransactionalHttpService } from "@idempotencykit/nestjs-postgres";
import { SqlServerTransactionalHttpModule, SqlServerTransactionalHttpService } from "@idempotencykit/nestjs-sqlserver";

const provider = process.env.IDEMPOTENCYKIT_INTEROP_PROVIDER;
assert.ok(["postgres", "sqlserver"].includes(provider), "Run the dedicated relational interop CI.");
const pgMode = provider === "postgres";
const scopeNamespace = "relational-http-crash-test";
const operationId = "create-order";
const subject = "crash-test-user";
const tenant = "crash-test-tenant";
const route = "/orders";
const scopeFor = key => computeScopeKey({
  namespace: scopeNamespace, operationId, principalId: subject, tenantId: tenant, clientKey: key,
});
const body = '{"quantity":2,"productId":50}';
const reordered = '{"productId":50,"quantity":2}';
const fingerprint = computeRequestFingerprint({ method: "POST", operationId, path: route, bodyJson: body });
const changed = '{"quantity":3,"productId":50}';
const hostDll = fileURLToPath(new URL(
  "../../../dotnet/tests/fixtures/IdempotencyKit.Relational.CrashHost/bin/Release/net10.0/IdempotencyKit.Relational.CrashHost.dll",
  import.meta.url,
));
let pool, app, dotnetBase, nestBase, dotnetConnection, runningHost, gate;
const hosts = new Set();
const newKey = () => "interop-" + randomUUID();

function deferred() {
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  return { promise, resolve };
}

async function bounded(promise, ms, message) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(message)), ms);
    })]);
  } finally { clearTimeout(timer); }
}

function startHost(stage = "retry") {
  const child = spawn("dotnet", [hostDll, provider, stage], {
    env: { ...process.env, IDEMPOTENCYKIT_CRASH_DB: dotnetConnection },
    stdio: ["ignore", "pipe", "pipe"],
  });
  const listeners = new Map();
  const seen = new Map();
  let stderr = "";
  child.stderr.on("data", data => { stderr = (stderr + data).slice(-4000); });
  const lines = createInterface({ input: child.stdout });
  lines.on("line", line => {
    for (const prefix of ["IK_CRASH_LISTENING:", "IK_CRASH_BLOCKED:"]) {
      if (line.startsWith(prefix)) {
        const value = line.slice(prefix.length);
        seen.set(prefix, value);
        listeners.get(prefix)?.resolve(value);
      }
    }
  });
  // Resolve rather than reject on process exit: no unhandled rejection if the
  // parent deliberately kills the host while no signal is being awaited.
  const exit = new Promise(resolve => {
    child.once("error", error => resolve({ error }));
    child.once("exit", (code, signal) => resolve({ code, signal }));
  });
  const host = {
    pid: child.pid,
    async wait(prefix) {
      if (seen.has(prefix)) return seen.get(prefix);
      const signal = deferred();
      listeners.set(prefix, signal);
      try {
        return await bounded(Promise.race([
          signal.promise, exit.then(status => {
            throw new Error("Kestrel exited before " + prefix + ": " + JSON.stringify(status) + " " + stderr);
          }),
        ]), 15000, "Kestrel did not emit " + prefix);
      } finally { listeners.delete(prefix); }
    },
    async stop() {
      if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
      const status = await bounded(exit, 10000, "Kestrel did not exit");
      lines.close();
      hosts.delete(host);
      return status;
    },
  };
  hosts.add(host);
  return host;
}

async function request(base, key, raw = body, authenticate = true) {
  const headers = { "Content-Type": "application/json", "Idempotency-Key": key };
  if (authenticate) headers["X-Test-User"] = subject;
  const response = await fetch(base + route, {
    method: "POST", headers, body: raw, signal: AbortSignal.timeout(20000),
  });
  return {
    status: response.status, location: response.headers.get("location"),
    retryAfter: response.headers.get("retry-after"),
    bytes: Buffer.from(await response.arrayBuffer()),
  };
}

function assertConflict(response) {
  assert.equal(response.status, 409, response.bytes.toString());
  assert.equal(JSON.parse(response.bytes).code, "IDEMPOTENCY_IN_PROGRESS");
  assert.ok(response.retryAfter);
}

async function requestWithRetry(base, key, raw = body) {
  for (let i = 0; i < 20; i++) {
    const r = await request(base, key, raw);
    if (r.status !== 409) return r;
    assertConflict(r);
    await sleep(100);
  }
  assert.fail("Same-key retries never completed.");
}

function assertCreated(r) {
  assert.equal(r.status, 201, r.bytes.toString());
  const payload = JSON.parse(r.bytes);
  assert.match(payload.id, /^[a-f0-9]{32}$/);
  assert.equal(payload.quantity, 2);
  assert.equal(r.location, route + "/" + payload.id);
}

function assertReplay(r, original) {
  assertCreated(r);
  assert.deepEqual(r.bytes, original.bytes);
  assert.equal(r.location, original.location);
}

async function readState(key) {
  const scope = scopeFor(key);
  let orders, records;
  if (pgMode) {
    orders = (await pool.query("SELECT id, quantity FROM ik_crash_orders WHERE request_key=$1", [key])).rows;
    records = (await pool.query(
      "SELECT scope_key_hash, request_fingerprint, state, response_body FROM idempotencykit_records WHERE scope_key_hash=$1",
      [scope],
    )).rows;
  } else {
    orders = (await pool.request().input("key", sql.VarChar(100), key)
      .query("SELECT id, quantity FROM dbo.ik_crash_orders WHERE request_key=@key")).recordset;
    records = (await pool.request().input("scope", sql.NVarChar(67), scope)
      .query("SELECT scope_key_hash, request_fingerprint, state, response_body FROM dbo.idempotencykit_records WHERE scope_key_hash=@scope")).recordset;
  }
  return { orders, records };
}

async function assertDurable(key, response) {
  const { orders, records } = await readState(key);
  assert.equal(orders.length, 1, "Exactly one business write");
  assert.equal(records.length, 1, "Exactly one idempotency row");
  assert.equal(orders[0].id, JSON.parse(response.bytes).id);
  assert.equal(orders[0].quantity, 2);
  assert.equal(records[0].state, "completed");
  assert.equal(records[0].scope_key_hash, scopeFor(key));
  assert.equal(records[0].request_fingerprint, fingerprint);
  assert.deepEqual(Buffer.from(records[0].response_body), response.bytes);
}

class TestAuth {
  canActivate(context) {
    const req = context.switchToHttp().getRequest();
    if (req.headers["x-test-user"] !== subject) throw new UnauthorizedException();
    req.user = { id: subject, tenantId: tenant };
    return true;
  }
}
Injectable()(TestAuth);

const HttpModule = pgMode ? PostgresTransactionalHttpModule : SqlServerTransactionalHttpModule;
const HttpService = pgMode ? PostgresTransactionalHttpService : SqlServerTransactionalHttpService;
class Orders {
  constructor(service) { this.service = service; }
  async create(req, res) {
    return this.service.handleExpress(req, res, operationId, async (transaction, raw, request) => {
      const id = randomUUID().replaceAll("-", "");
      const key = request.headers["idempotency-key"];
      const { quantity } = JSON.parse(raw);
      if (pgMode) {
        await transaction.query("INSERT INTO ik_crash_orders (id, request_key, quantity) VALUES ($1,$2,$3)",
          [id, key, quantity]);
      } else {
        await new sql.Request(transaction).input("id", sql.VarChar(32), id)
          .input("key", sql.VarChar(100), key).input("qty", sql.Int, quantity)
          .query("INSERT INTO dbo.ik_crash_orders (id, request_key, quantity) VALUES (@id,@key,@qty)");
      }
      const activeGate = gate;
      if (activeGate?.key === key) {
        activeGate.entered.resolve();
        await bounded(activeGate.release.promise, 18000, "Test did not release held transaction");
      }
      return { statusCode: 201, contentType: "application/json",
        body: Buffer.from(JSON.stringify({ id, quantity })),
        headers: { Location: route + "/" + id } };
    });
  }
}
Inject(HttpService)(Orders, undefined, 0);
Controller("orders")(Orders);
UseGuards(TestAuth)(Orders);
Post()(Orders.prototype, "create", Object.getOwnPropertyDescriptor(Orders.prototype, "create"));
Req()(Orders.prototype, "create", 0);
Res()(Orders.prototype, "create", 1);

before(async () => {
  // Deliberately use isolated, loopback-only CI services and reserved test DB names.
  const migration = await readFile(new URL(
    "../../../spec/sql/" + provider + "/001_idempotencykit_records.sql", import.meta.url,
  ), "utf8");
  if (pgMode) {
    const url = new URL(process.env.IDEMPOTENCYKIT_INTEROP_POSTGRES_URL);
    assert.equal(url.hostname, "127.0.0.1");
    assert.equal(url.pathname, "/idk_interop");
    dotnetConnection = process.env.IDEMPOTENCYKIT_INTEROP_DOTNET_DB;
    assert.match(dotnetConnection, /^Host=127\.0\.0\.1;Port=5432;Database=idk_interop;/);
    pool = new pg.Pool({ connectionString: url.href, max: 20 });
    pool.on("error", () => {}); // Request failures remain visible to the tests.
    await pool.query(migration);
    await pool.query("CREATE TABLE ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity integer NOT NULL)");
  } else {
    const password = process.env.IDEMPOTENCYKIT_INTEROP_SQLSERVER_PASSWORD;
    assert.ok(password);
    const db = "idk_interop_" + randomUUID().replaceAll("-", "");
    const config = { server: "127.0.0.1", port: 1433, user: "sa", password,
      options: { encrypt: true, trustServerCertificate: true },
      pool: { max: 20, min: 0 }, connectionTimeout: 3000, requestTimeout: 15000 };
    let master;
    for (let attempt = 0; attempt < 80; attempt++) {
      const candidate = new sql.ConnectionPool({ ...config, database: "master" });
      try { master = await candidate.connect(); break; }
      catch (error) {
        await candidate.close().catch(() => {});
        if (attempt === 79) throw error;
        await sleep(1000);
      }
    }
    try {
      await master.request().query("CREATE DATABASE [" + db + "]");
    } finally { await master.close(); }
    pool = await new sql.ConnectionPool({ ...config, database: db }).connect();
    // CI password is supplied by this job; escape it as an ADO.NET quoted value.
    dotnetConnection = 'Server=127.0.0.1,1433;Database=' + db +
      ';User ID=sa;Password="' + password.replaceAll('"', '""') +
      '";Encrypt=True;TrustServerCertificate=True;Connect Timeout=5';
    await pool.request().query(migration);
    await pool.request().query("CREATE TABLE dbo.ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity int NOT NULL)");
  }
  const executor = pgMode ? new PostgresTransactionalExecutor(pool, 300) :
    new SqlServerTransactionalExecutor(pool, 300);
  class FixtureModule {}
  Module({ imports: [HttpModule.forRoot({
    namespace: scopeNamespace, executor, retentionMs: 3600000,
    resolveCaller: req => ({ principalId: req.user.id, tenantId: req.user.tenantId }),
  })], controllers: [Orders] })(FixtureModule);
  app = await NestFactory.create(FixtureModule, { rawBody: true, logger: false });
  await app.listen(0, "127.0.0.1");
  nestBase = "http://127.0.0.1:" + app.getHttpServer().address().port;
  runningHost = startHost();
  dotnetBase = await runningHost.wait("IK_CRASH_LISTENING:");
}, { timeout: 240000 });

after(async () => {
  gate?.release.resolve();
  await Promise.allSettled([...hosts].map(host => host.stop()));
  try { await app?.close(); }
  finally { if (pool) await (pgMode ? pool.end() : pool.close()); }
}, { timeout: 30000 });

test("ASP.NET -> NestJS and NestJS -> ASP.NET replay exact bytes, headers and canonical fingerprints", { timeout: 60000 }, async () => {
  for (const [first, second] of [[dotnetBase, nestBase], [nestBase, dotnetBase]]) {
    const key = newKey();
    const original = await request(first, key);
    assertCreated(original);
    assertReplay(await request(second, key, reordered), original);
    assert.equal((await request(second, key, changed)).status, 422);
    assert.equal((await request(second, key, body, false)).status, 401);
    await assertDurable(key, original);
  }
});

test("NestJS holds an uncommitted claim; Kestrel reports in-progress and then replays", { timeout: 60000 }, async () => {
  const key = newKey();
  gate = { key, entered: deferred(), release: deferred() };
  const pending = request(nestBase, key).then(r => ({ r }), error => ({ error }));
  try {
    await bounded(gate.entered.promise, 10000, "NestJS never entered business transaction");
    assertConflict(await request(dotnetBase, key, reordered));
  } finally {
    gate.release.resolve();
    gate = undefined;
  }
  const { r, error } = await pending;
  if (error) throw error;
  assertCreated(r);
  assertReplay(await request(dotnetBase, key, reordered), r);
  await assertDurable(key, r);
});

test("Kestrel dies before COMMIT while NestJS competes; same key recovers once", { timeout: 60000 }, async () => {
  const key = newKey();
  const holder = startHost("before-commit");
  let pending;
  try {
    const url = await holder.wait("IK_CRASH_LISTENING:");
    pending = request(url, key).then(r => ({ r }), error => ({ error }));
    assert.equal(await holder.wait("IK_CRASH_BLOCKED:"), "before-commit");
    assertConflict(await request(nestBase, key, reordered));
    const status = await holder.stop();
    assert.equal(status.signal, "SIGKILL");
    assert.ok((await pending).error, "Killed host must not deliver response");
    const recovered = await requestWithRetry(nestBase, key, reordered);
    assertCreated(recovered);
    assertReplay(await request(dotnetBase, key), recovered);
    await assertDurable(key, recovered);
  } finally { if (hosts.has(holder)) await holder.stop(); }
});

test("Kestrel dies after COMMIT; NestJS replays the original stored result", { timeout: 60000 }, async () => {
  const key = newKey();
  const holder = startHost("after-commit");
  try {
    const url = await holder.wait("IK_CRASH_LISTENING:");
    const pending = request(url, key).then(r => ({ r }), error => ({ error }));
    assert.equal(await holder.wait("IK_CRASH_BLOCKED:"), "after-commit");
    const { orders, records } = await readState(key);
    assert.equal(orders.length, 1);
    assert.equal(records.length, 1);
    assert.equal(records[0].state, "completed");
    const expected = { bytes: Buffer.from(records[0].response_body), location: route + "/" + orders[0].id };
    assert.equal((await holder.stop()).signal, "SIGKILL");
    assert.ok((await pending).error, "Killed host must not deliver response");
    const replay = await request(nestBase, key, reordered);
    assertReplay(replay, expected);
    await assertDurable(key, replay);
  } finally { if (hosts.has(holder)) await holder.stop(); }
});

test("24 concurrent mixed-framework requests produce one committed result", { timeout: 120000 }, async () => {
  const key = newKey();
  const results = await Promise.all(Array.from({ length: 24 }, (_, i) =>
    requestWithRetry(i % 2 ? nestBase : dotnetBase, key, i % 2 ? reordered : body),
  ));
  for (const result of results) assertReplay(result, results[0]);
  await assertDurable(key, results[0]);
});

test("bounded HTTP performance baseline preserves durable correctness", {
  skip: process.env.IDEMPOTENCYKIT_PERF_BASELINE !== "1",
  timeout: 180000,
}, async () => {
  for (const [runtime,base,pid,memScope] of [
    ["dotnet",dotnetBase,runningHost.pid,"Kestrel process RSS"],
    ["node",nestBase,process.pid,"NestJS + test/load-client process RSS"],
  ]) {
    await runHttpBaseline({provider,runtime,
      request:(k,alternate)=>request(base,k,alternate?reordered:body),
      verify:assertDurable,readRss:()=>readRssMiB(pid),memScope});
  }
});
