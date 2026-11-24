// Dedicated CI: both real HTTP frameworks against ONE disposable database.
// Synthetic authentication is test-only. Never point this lab at production.
import "reflect-metadata";
import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawn, execFile } from "node:child_process";
import { promisify } from "node:util";
import { createInterface } from "node:readline";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { setTimeout as sleep } from "node:timers/promises";
import { runHttpBaseline, readRssMiB } from "../support/http-performance-baseline.mjs";
import { runSustainedHttpStress } from "../support/http-sustained-stress.mjs";
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
const stress = process.env.IDEMPOTENCYKIT_STRESS === "1";
const poolLimit = stress ? 8 : 20;
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
  let blockedCount=0;
  let stderr = "";
  child.stderr.on("data", data => { stderr = (stderr + data).slice(-4000); });
  const lines = createInterface({ input: child.stdout });
  lines.on("line", line => {
    for (const prefix of ["IK_CRASH_LISTENING:", "IK_CRASH_BLOCKED:"]) {
      if (line.startsWith(prefix)) {
        if(prefix==="IK_CRASH_BLOCKED:")blockedCount++;
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
    async waitForBlocked(count){
      const end=performance.now()+15000;
      while(blockedCount<count&&performance.now()<end){
        assert.equal(child.exitCode,null,"Kestrel exited while saturating pool");
        await sleep(20);
      }
      assert.equal(blockedCount,count,"All fixture connections must be held");
    },
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
    pool = new pg.Pool({ connectionString: url.href, max: poolLimit, ...(stress && { connectionTimeoutMillis: 3000 }) });
    pool.on("error", () => {}); // Request failures remain visible to the tests.
    await pool.query(migration);
    await pool.query("CREATE TABLE ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity integer NOT NULL)");
  } else {
    const password = process.env.IDEMPOTENCYKIT_INTEROP_SQLSERVER_PASSWORD;
    assert.ok(password);
    const db = "idk_interop_" + randomUUID().replaceAll("-", "");
    const config = { server: "127.0.0.1", port: 1433, user: "sa", password,
      options: { encrypt: true, trustServerCertificate: true },
      pool: { max: poolLimit, min: 0, ...(stress && { acquireTimeoutMillis: 3000 }) }, connectionTimeout: 3000, requestTimeout: 15000 };
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
    pool.on("error", () => {}); // Outage is asserted at the HTTP and durable-state boundaries.
    // CI password is supplied by this job; escape it as an ADO.NET quoted value.
    dotnetConnection = 'Server=127.0.0.1,1433;Database=' + db +
      ';User ID=sa;Password="' + password.replaceAll('"', '""') +
      '";Encrypt=True;TrustServerCertificate=True;Connect Timeout=5';
    await pool.request().query(migration);
    await pool.request().query("CREATE TABLE dbo.ik_crash_orders (id varchar(32) PRIMARY KEY, request_key varchar(100) NOT NULL, quantity int NOT NULL)");
  }
  if(stress) dotnetConnection += pgMode ? ";Maximum Pool Size=8;Timeout=3" : ";Max Pool Size=8;Connect Timeout=3";
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

test("sustained three-level HTTP load across each runtime", {skip:!stress,timeout:300000}, async()=>{
  for(const [runtime,base,pid,memScope] of [
    ["dotnet",dotnetBase,runningHost.pid,"Kestrel process RSS"],
    ["node",nestBase,process.pid,"NestJS + test/load-client process RSS"],
  ]){
    await runSustainedHttpStress({provider,runtime,
      request:(k,kind)=>request(base,k,kind==="changed"?changed:kind==="reordered"?reordered:body),
      verify:assertDurable,readRss:()=>readRssMiB(pid),memScope,
      readPool:runtime==="node"?()=>pgMode?
        {total:pool.totalCount,idle:pool.idleCount,waiting:pool.waitingCount}:
        {size:pool.size,available:pool.available,borrowed:pool.borrowed,pending:pool.pending}:()=>null});
  }
});

function assertUnavailable(r){
  assert.equal(r.status,503,r.bytes.toString());
  assert.equal(JSON.parse(r.bytes).code,"IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
}
async function retryAfterStorage(base,key){
  const deadline=performance.now()+60000;
  for(let i=0;i<30&&performance.now()<deadline;i++){
    const r=await request(base,key);
    if(r.status===201){assertCreated(r);return r;}
    if(r.status===409)assertConflict(r);else assertUnavailable(r);
    await sleep(Math.min(250+i*150,2000));
  }
  assert.fail("Storage recovery did not complete");
}
test("exhaust NestJS connection pool then recover without duplicate writes", {skip:!stress,timeout:60000}, async()=>{
  const leases=[];
  const oldKey=newKey(),original=await request(nestBase,oldKey);
  assertCreated(original);
  const newOperation=newKey();
  try{
    for(let i=0;i<poolLimit;i++){
      if(pgMode){
        const client=await pool.connect();
        leases.push(()=>client.release());
      }else{
        const tx=new sql.Transaction(pool);
        await tx.begin();
        leases.push(()=>tx.rollback());
      }
    }
    // Real driver occupancy, not an artificial mocked timeout.
    if(pgMode){assert.equal(pool.totalCount,poolLimit);assert.equal(pool.idleCount,0);}
    else assert.equal(pool.borrowed,poolLimit);
    const responses=await Promise.all([request(nestBase,newOperation),request(nestBase,oldKey)]);
    for(const r of responses)assertUnavailable(r);
  }finally{
    await Promise.all(leases.map(release=>release()));
  }
  let missing=await readState(newOperation);
  assert.equal(missing.orders.length,0);assert.equal(missing.records.length,0);
  assertReplay(await retryAfterStorage(nestBase,oldKey),original);
  const recovered=await retryAfterStorage(nestBase,newOperation);
  assertReplay(await request(dotnetBase,newOperation),recovered);
  await assertDurable(newOperation,recovered);await assertDurable(oldKey,original);
  console.log("IDK_POOL_RECOVERY "+provider+" node");
});

test("exhaust Kestrel connection pool then recover on the same host", {skip:!stress,timeout:90000}, async()=>{
  const holder=startHost("pool-hold");
  let pending=[];
  try{
    const base=await holder.wait("IK_CRASH_LISTENING:");
    const oldKey=newKey(),original=await request(base,oldKey);
    assertCreated(original);
    const heldKeys=Array.from({length:poolLimit},()=> "pool-held-"+randomUUID());
    pending=heldKeys.map(key=>request(base,key).then(r=>({r}),error=>({error})));
    await holder.waitForBlocked(poolLimit);
    const newOperation=newKey();
    // The fixture holds all eight leased physical connections for 12 seconds.
    // Connection acquisition timeout is 3 seconds; no extra connection is available.
    const responses=await Promise.all([request(base,newOperation),request(base,oldKey)]);
    for(const r of responses)assertUnavailable(r);
    const results=await Promise.all(pending);
    for(let i=0;i<results.length;i++){
      if(results[i].error)throw results[i].error;
      assertCreated(results[i].r);
      await assertDurable(heldKeys[i],results[i].r);
    }
    const missing=await readState(newOperation);
    assert.equal(missing.orders.length,0);assert.equal(missing.records.length,0);
    assertReplay(await retryAfterStorage(base,oldKey),original);
    const recovered=await retryAfterStorage(base,newOperation);
    assertReplay(await request(nestBase,newOperation),recovered);
    await assertDurable(newOperation,recovered);await assertDurable(oldKey,original);
    console.log("IDK_POOL_RECOVERY "+provider+" dotnet");
  }finally{
    if(hosts.has(holder))await holder.stop();
    await Promise.allSettled(pending);
  }
});

test("actual disposable database stop/start: fail closed and same-key recovery", {
  skip:!stress,timeout:240000,
},async()=>{
  const container=process.env.IDEMPOTENCYKIT_OUTAGE_CONTAINER;
  assert.equal(process.env.GITHUB_ACTIONS,"true","Only the dedicated CI service may be stopped");
  assert.match(container??"",/^[a-f0-9]{64}$/);
  const run=promisify(execFile);
  const docker=async(...args)=>(await run("docker",args,{timeout:45000})).stdout.trim();
  const image=await docker("inspect","--format","{{.Config.Image}}",container);
  assert.equal(image,pgMode?"postgres:17":"mcr.microsoft.com/mssql/server:2022-latest");
  const oldKey=newKey(),original=await request(dotnetBase,oldKey);
  assertCreated(original);
  assertReplay(await request(nestBase,oldKey),original);
  const freshDotnet=newKey(),freshNode=newKey(),interrupted=newKey();
  // A live Node transaction has already inserted its order, but has not committed.
  gate={key:interrupted,entered:deferred(),release:deferred()};
  const active=request(nestBase,interrupted).then(r=>({r}),error=>({error}));
  let stopped=false;
  try{
    await bounded(gate.entered.promise,10000,"No active transaction to interrupt");
    await docker("stop","--time","10",container);
    stopped=true;
    assert.equal(await docker("inspect","--format","{{.State.Running}}",container),"false");
    gate.release.resolve();gate=undefined;
    const cut=await active;
    if(cut.r)assertUnavailable(cut.r);
    else assert.ok(cut.error,"Interrupted operation must not deliver success");
    const offline=await Promise.all([
      request(dotnetBase,freshDotnet),request(dotnetBase,oldKey),
      request(nestBase,freshNode),request(nestBase,oldKey),
    ]);
    for(const r of offline)assertUnavailable(r);
    console.log("IDK_DATABASE_OFFLINE "+provider+" 4/4 fail-closed");
  }finally{
    gate?.release.resolve();gate=undefined;
    // Start the exact existing CI service; never remove it or create a new DB.
    if(stopped||await docker("inspect","--format","{{.State.Running}}",container)==="false")
      await docker("start",container);
    await active;
  }
  const deadline=performance.now()+60000;
  for(;;){
    try{
      if(pgMode)await pool.query("SELECT 1");else await pool.request().query("SELECT 1");
      break;
    }catch(e){if(performance.now()>deadline)throw e;await sleep(1000);}
  }
  // Durable proof before any retry: the interrupted transaction rolled back.
  for(const key of [interrupted,freshDotnet,freshNode]){
    const state=await readState(key);
    assert.equal(state.orders.length,0);assert.equal(state.records.length,0);
  }
  assertReplay(await retryAfterStorage(dotnetBase,oldKey),original);
  assertReplay(await retryAfterStorage(nestBase,oldKey),original);
  for(const [base,other,key] of [
    [dotnetBase,nestBase,freshDotnet],[nestBase,dotnetBase,freshNode],[nestBase,dotnetBase,interrupted],
  ]){
    const recovered=await retryAfterStorage(base,key);
    assertReplay(await retryAfterStorage(other,key),recovered);
    await assertDurable(key,recovered);
  }
  await assertDurable(oldKey,original);
  console.log("IDK_DATABASE_RECOVERY "+provider+" original replay + 3 safe recoveries");
});
