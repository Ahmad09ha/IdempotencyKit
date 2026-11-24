
import assert from "node:assert/strict";
import { runHttpBaseline, readRssMiB } from "./http-performance-baseline.mjs";

const [runtime,portText,pidText]=process.argv.slice(2);
assert.ok(["dotnet","node"].includes(runtime));
const port=Number(portText),pid=Number(pidText);
assert.ok(Number.isInteger(port)&&port>0&&port<=65535);
assert.ok(Number.isInteger(pid)&&pid>0);
const base="http://127.0.0.1:"+port;
const auth={Authorization:"Bearer orders-demo"};
async function count(){
  const r=await fetch(base+"/orders",{headers:auth,signal:AbortSignal.timeout(15000)});
  assert.equal(r.status,200);
  return (await r.json()).count;
}
const before=await count();
const seen=new Map();
const result=await runHttpBaseline({provider:"sqlite",runtime,
  readRss:()=>readRssMiB(pid),memScope:"separate server process RSS",
  async request(key,alternate){
    const r=await fetch(base+"/orders",{
      method:"POST",headers:{...auth,"Content-Type":"application/json","Idempotency-Key":key},
      body:alternate?'{"quantity":2,"productId":50}':'{"productId":50,"quantity":2}',
      signal:AbortSignal.timeout(15000)});
    return {status:r.status,location:r.headers.get("location"),bytes:Buffer.from(await r.arrayBuffer())};
  },
  async verify(key,response){
    if(seen.has(key))assert.equal(seen.get(key),response.location);
    else seen.set(key,response.location);
    assert.match(response.location,/^\/orders\/\d+$/);
    const r=await fetch(base+response.location,{headers:auth,signal:AbortSignal.timeout(15000)});
    assert.equal(r.status,200);
    assert.deepEqual(await r.json(),JSON.parse(response.bytes));
  },
});
assert.equal(seen.size,result.committedOrders);
assert.equal(new Set(seen.values()).size,result.committedOrders);
assert.equal(await count(),before+result.committedOrders);
console.log("SQLite baseline verified exactly "+result.committedOrders+" additional orders.");
