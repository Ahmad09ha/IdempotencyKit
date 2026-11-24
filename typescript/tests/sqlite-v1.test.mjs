import test, { before, after } from "node:test";
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { fork } from "node:child_process";
import { mkdtempSync, rmSync, existsSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";
import { computeScopeKey, computeRequestFingerprint } from "@idempotencykit/core";
import {
  initializeSqliteDatabase, SqliteIdempotencyStore,
  SqliteTransactionalExecutor,
} from "@idempotencykit/sqlite";

const folder = mkdtempSync(join(tmpdir(), "idempotencykit-sqlite-test-"));
const file = join(folder, "orders.db");
let store, executor;
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

function open() { return new DatabaseSync(file, { timeout: 1_000 }); }
function count(table, key) {
  assert.ok(["orders_sqlite_test", "idempotencykit_records"].includes(table));
  const db = open();
  try {
    return db.prepare(
      "SELECT COUNT(*) AS total FROM " + table + " WHERE scope_key_hash = ?",
    ).get(key).total;
  } finally { db.close(); }
}
function key() {
  return computeScopeKey({
    namespace:"sqlite-node-test", operationId:"orders.create.v1",
    principalId:"alice", tenantId:"acme", clientKey:"key-" + randomUUID(),
  });
}
function fingerprint(qty) {
  return computeRequestFingerprint({
    method:"POST", operationId:"orders.create.v1", path:"/orders",
    bodyJson:JSON.stringify({ quantity:qty }),
  });
}
function response(id,qty) {
  return { statusCode:201, contentType:"application/json",
    body:Buffer.from(JSON.stringify({ id,quantity:qty })),
    headers:{ Location:"/orders/"+id } };
}
function insert(session,scope,qty) {
  const result = session.prepare(
    "INSERT INTO orders_sqlite_test(scope_key_hash,quantity) VALUES (?,?)",
  ).run(scope,qty);
  return response(Number(result.lastInsertRowid),qty);
}

before(() => {
  initializeSqliteDatabase(file);
  const db = open();
  try {
    db.exec(`
      CREATE TABLE IF NOT EXISTS orders_sqlite_test (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        scope_key_hash TEXT NOT NULL,
        quantity INTEGER NOT NULL
      );`);
  } finally { db.close(); }
  store = new SqliteIdempotencyStore(file);
  executor = new SqliteTransactionalExecutor(file);
});
after(() => { rmSync(folder, { recursive:true, force:true }); });

test("SQLite initializes actual WAL on-disk file with FULL synchronous settings", () => {
  assert.equal(existsSync(file),true);
  const db=open();
  try {
    assert.equal(db.prepare("PRAGMA journal_mode").get().journal_mode,"wal");
    db.exec("PRAGMA synchronous = FULL");
    assert.equal(db.prepare("PRAGMA synchronous").get().synchronous,2);
  } finally { db.close(); }
  assert.throws(() => new SqliteIdempotencyStore(":memory:"));
  assert.throws(() => new SqliteTransactionalExecutor("file:orders?mode=memory"));
});

test("Node SQLite schema matches the published source migration", () => {
  const sql = readFileSync(new URL(
    "../../spec/sql/sqlite/001_idempotencykit_records.sql", import.meta.url), "utf8");
  assert.match(sql,/CREATE TABLE IF NOT EXISTS idempotencykit_records/);
  assert.match(sql,/CREATE INDEX IF NOT EXISTS ix_idempotencykit_expiry/);
  assert.match(sql,/state IN \('processing','completed','uncertain'\)/);
});

test("Standard mode claims owners, extends lease, rejects mismatches, replays bytes after reopen", async () => {
  const scope=key(), hash=fingerprint(1);
  const first=await store.tryClaim(scope,hash,60_000);
  assert.equal(first.status,"acquired");
  assert.equal((await store.tryClaim(scope,hash,60_000)).status,"in_progress");
  assert.equal(await store.tryComplete(scope,hash,randomUUID(),
    response(3,1),86_400_000),false);
  assert.equal(await store.tryExtendLease(scope,randomUUID(),60_000),false);
  assert.equal(await store.tryExtendLease(scope,first.ownerToken,60_000),true);
  assert.equal(await store.tryComplete(scope,hash,first.ownerToken,
    response(3,1),86_400_000),true);
  assert.equal(await store.tryComplete(scope,hash,first.ownerToken,
    response(4,1),86_400_000),false);
  const another=new SqliteIdempotencyStore(file);
  const replay=await another.tryClaim(scope,hash,60_000);
  assert.equal(replay.status,"completed");
  assert.deepEqual(Buffer.from(replay.response.body),response(3,1).body);
  assert.equal(replay.response.headers.location,"/orders/3");
  assert.equal((await another.tryClaim(scope,fingerprint(2),60_000)).status,
    "fingerprint_mismatch");
});

test("Expired Standard claim becomes uncertain and never automatically reclaims", async () => {
  const scope=key(), hash=fingerprint(2);
  const claim=await store.tryClaim(scope,hash,20);
  assert.equal(claim.status,"acquired");
  await wait(110);
  assert.equal((await store.tryClaim(scope,hash,60_000)).status,"uncertain");
  assert.equal(await store.tryComplete(scope,hash,claim.ownerToken,
    response(1,2),86_400_000),false);
  assert.equal((await store.tryClaim(scope,hash,86_400_000)).status,"uncertain");
});

test("Transactional SQLite stores business insert + replay in one durable COMMIT", async () => {
  const scope=key(), hash=fingerprint(3);
  let calls=0;
  const first=await executor.execute(scope,hash,async session => {
    calls++;
    return insert(session,scope,3);
  },86_400_000);
  assert.equal(first.status,"executed");
  assert.equal(count("orders_sqlite_test",scope),1);
  assert.equal(count("idempotencykit_records",scope),1);
  const another=new SqliteTransactionalExecutor(file);
  const replay=await another.execute(scope,hash,async () => {
    calls++;
    throw new Error("Replay must not run handler.");
  },86_400_000);
  assert.equal(replay.status,"replayed");
  assert.deepEqual(Buffer.from(replay.response.body),Buffer.from(first.response.body));
  assert.equal(replay.response.headers.location,first.response.headers.location);
  assert.equal(calls,1);
  const mismatch=await another.execute(scope,fingerprint(4),
    async () => { throw new Error("Mismatch must not run."); },86_400_000);
  assert.equal(mismatch.status,"fingerprint_mismatch");
  assert.equal((await store.tryClaim(scope,hash,30_000)).status,"completed");
});

test("Domain throw and unsafe replay capture roll back both domain and key", async () => {
  const scope=key(), hash=fingerprint(5);
  await assert.rejects(()=>executor.execute(scope,hash,async session => {
    insert(session,scope,5);
    throw new Error("Fault after SQL INSERT");
  },86_400_000));
  assert.equal(count("orders_sqlite_test",scope),0);
  assert.equal(count("idempotencykit_records",scope),0);
  await assert.rejects(()=>executor.execute(scope,hash,async session => {
    const captured=insert(session,scope,5);
    return { ...captured,headers:{"Set-Cookie":"token=unsafe"} };
  },86_400_000));
  assert.equal(count("orders_sqlite_test",scope),0);
  assert.equal(count("idempotencykit_records",scope),0);
  const done=await executor.execute(scope,hash,
    async session=>insert(session,scope,5),86_400_000);
  assert.equal(done.status,"executed");
  assert.equal(count("orders_sqlite_test",scope),1);
});

test("100 concurrent same-key calls through separate adapters insert one domain row", async () => {
  const scope=key(), hash=fingerprint(6);
  let calls=0;
  const result=await Promise.all(Array.from({length:100},()=>(
    new SqliteTransactionalExecutor(file).execute(scope,hash,async session => {
      calls++;
      await wait(3); // Async callbacks remain serialized by the local file gate.
      return insert(session,scope,6);
    },86_400_000)
  )));
  assert.equal(result.filter(x=>x.status==="executed").length,1);
  assert.equal(calls,1);
  assert.equal(count("orders_sqlite_test",scope),1);
  assert.equal(count("idempotencykit_records",scope),1);
  assert.ok(result.every(x=>["executed","replayed"].includes(x.status)));
});

test("cleanup only removes expired completed records, not uncertain rows", async () => {
  const doneKey=key(), unknownKey=key();
  const completed=await store.tryClaim(doneKey,fingerprint(10),60_000);
  assert.equal(await store.tryComplete(doneKey,fingerprint(10),
    completed.ownerToken,response(1,10),1),true);
  const claimed=await store.tryClaim(unknownKey,fingerprint(11),60_000);
  assert.equal(await store.tryMarkUncertain(unknownKey,fingerprint(11),
    claimed.ownerToken),true);
  await wait(70);
  assert.ok(await store.purgeExpiredCompleted(100)>=1);
  assert.equal(count("idempotencykit_records",doneKey),0);
  assert.equal(count("idempotencykit_records",unknownKey),1);
});

test("corrupt stored completed response must fail closed rather than leak an unsafe header", async () => {
  const scope=key(), hash=fingerprint(12);
  const claim=await store.tryClaim(scope,hash,60_000);
  assert.equal(await store.tryComplete(scope,hash,claim.ownerToken,
    response(1,12),60_000),true);
  const db=open();
  try {
    db.prepare(
      "UPDATE idempotencykit_records SET response_headers=? WHERE scope_key_hash=?",
    ).run('{"set-cookie":"secret=1"}',scope);
  } finally { db.close(); }
  await assert.rejects(()=>store.tryClaim(scope,hash,60_000));
});

test("transaction session rejects manual COMMIT and remains atomic", async () => {
  const scope=key(), hash=fingerprint(13);
  await assert.rejects(()=>executor.execute(scope,hash,async session => {
    insert(session,scope,13);
    session.prepare("COMMIT");
    return response(1,13);
  },86_400_000));
  assert.equal(count("orders_sqlite_test",scope),0);
  assert.equal(count("idempotencykit_records",scope),0);
});

function forkWorker(mode,scope,hash) {
  const worker=fork(fileURLToPath(new URL(
    "./support/sqlite-crash-worker.mjs",import.meta.url)),
    [mode,file,scope,hash],{
      env:process.env,stdio:["ignore","ignore","pipe","ipc"],
    });
  let errors="";
  worker.stderr?.on("data",chunk=>{errors+=String(chunk);});
  return {worker,errors:()=>errors};
}
function waitPhase(worker,name,errors) {
  return new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{
      cleanup();
      reject(new Error("SQLite crash worker timed out: "+name+" "+errors()));
    },12_000);
    const message=entry=>{
      if(entry?.phase!==name)return;
      cleanup();
      resolve(entry);
    };
    const terminated=(code,signal)=>{
      cleanup();
      reject(new Error("SQLite worker exited before "+name+": "+
        code+"/"+signal+" "+errors()));
    };
    const failure=error=>{cleanup();reject(error);};
    const cleanup=()=>{
      clearTimeout(timer);
      worker.off("message",message);
      worker.off("exit",terminated);
      worker.off("error",failure);
    };
    worker.on("message",message);
    worker.on("exit",terminated);
    worker.on("error",failure);
  });
}
async function killWorker(worker) {
  if(worker.exitCode!==null||worker.signalCode!==null)return;
  const stopped=new Promise(resolve=>worker.once("exit",resolve));
  assert.equal(worker.kill("SIGKILL"),true);
  await stopped;
}

test("unrelated process writer causes SQLITE_BUSY STORAGE ERROR, never InProgress",async()=>{
  const scope=key(),hash=fingerprint(20);
  const {worker,errors}=forkWorker("unrelated-writer",scope,hash);
  try {
    await waitPhase(worker,"locked",errors);
    const busyStore=new SqliteIdempotencyStore(file,50);
    await assert.rejects(()=>busyStore.tryClaim(scope,hash,86_400_000),
      e=>/locked|busy/i.test(e.message));
    assert.equal(count("idempotencykit_records",scope),0);
  } finally {await killWorker(worker);}
  const claimed=await store.tryClaim(scope,hash,86_400_000);
  assert.equal(claimed.status,"acquired");
});

test("SIGKILL before COMMIT loses both SQLite domain INSERT and claim",async()=>{
  const scope=key(),hash=fingerprint(21);
  const {worker,errors}=forkWorker("before-commit",scope,hash);
  try {await waitPhase(worker,"before-commit",errors);}
  finally {await killWorker(worker);}
  assert.equal(count("orders_sqlite_test",scope),0);
  assert.equal(count("idempotencykit_records",scope),0);
  const again=await executor.execute(scope,hash,
    async session=>insert(session,scope,7),86_400_000);
  assert.equal(again.status,"executed");
  assert.equal(count("orders_sqlite_test",scope),1);
});

test("SIGKILL after COMMIT before caller ACK replays saved bytes without new SQL",async()=>{
  const scope=key(),hash=fingerprint(22);
  const {worker,errors}=forkWorker("after-commit",scope,hash);
  let saved;
  try {saved=await waitPhase(worker,"after-commit",errors);}
  finally {await killWorker(worker);}
  assert.equal(count("orders_sqlite_test",scope),1);
  assert.equal(count("idempotencykit_records",scope),1);
  const replay=await new SqliteTransactionalExecutor(file).execute(
    scope,hash,async()=>{throw new Error("Killed process already committed.");},
    86_400_000);
  assert.equal(replay.status,"replayed");
  assert.equal(Buffer.from(replay.response.body).toString("utf8"),saved.response);
  assert.equal(count("orders_sqlite_test",scope),1);
});

test("missing processing lease becomes uncertain without a second domain callback", async () => {
  const scope=key(), hash=fingerprint(30);
  const claimed=await store.tryClaim(scope,hash,60_000);
  assert.equal(claimed.status,"acquired");
  const db=open();
  try {
    db.prepare(
      "UPDATE idempotencykit_records SET lease_expires_at=NULL WHERE scope_key_hash=?",
    ).run(scope);
  } finally { db.close(); }
  assert.equal((await executor.execute(scope,hash,async()=>{
    throw new Error("Corrupt lease cannot trigger domain SQL.");
  },86_400_000)).status,"uncertain");
  assert.equal((await store.tryClaim(scope,hash,60_000)).status,"uncertain");
});
