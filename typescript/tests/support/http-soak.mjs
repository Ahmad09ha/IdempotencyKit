import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { performance } from "node:perf_hooks";
import { setTimeout as sleep } from "node:timers/promises";

export function soakSeconds() {
  const seconds=Number(process.env.IDEMPOTENCYKIT_SOAK_SECONDS??"0");
  assert.ok(Number.isInteger(seconds)&&(seconds===0||seconds>=60&&seconds<=300),
    "Soak duration must be zero or 60–300 seconds");
  return seconds;
}
const key=()=> "soak-"+randomUUID();
function valid(r){
  assert.equal(r.status,201,r.bytes.toString());
  assert.ok(r.location);
  assert.equal(JSON.parse(r.bytes).quantity,2);
}
function same(r,expected){
  valid(r);assert.equal(r.location,expected.location);assert.deepEqual(r.bytes,expected.bytes);
}
function retryable(r){
  if(r.status===409){assert.equal(JSON.parse(r.bytes).code,"IDEMPOTENCY_IN_PROGRESS");return true;}
  if(r.status===503){assert.equal(JSON.parse(r.bytes).code,"IDEMPOTENCY_TRANSACTION_UNAVAILABLE");return true;}
  return false;
}
function transport(e){return ["TimeoutError","AbortError"].includes(e?.name)||(e instanceof TypeError&&e.message==="fetch failed");}
export async function runHttpSoak({provider,runtime,request,verify,readRss,memScope,readPool=()=>null}){
  const seconds=soakSeconds();
  assert.ok(seconds>=60);
  const concurrency=16,seed=new Map(),created=new Map(),locations=new Set();
  async function recover(k){
    for(let i=0;i<20;i++){
      try{
        const r=await request(k,"original");
        if(r.status===201){valid(r);return r;}
        assert.ok(retryable(r),r.bytes.toString());
      }catch(e){if(!transport(e))throw e;}
      await sleep(250);
    }
    assert.fail("Same-key recovery exhausted");
  }
  for(let i=0;i<32;i++){
    const k=key(),r=await recover(k);
    assert.ok(!locations.has(r.location));locations.add(r.location);
    seed.set(k,r);await verify(k,r);
  }
  const seeds=[...seed.keys()];
  // Bounded retained test data: fixed seed + up to 512 fresh keys. Timed
  // latency uses constant-size buckets rather than retaining every sample.
  const limits=[1,2,5,10,20,50,100,200,500,1000,2000,5000,10000,20000,Infinity];
  const buckets=Array(limits.length).fill(0),statuses={},windows=[];
  let issued=0,completed=0,firstError,maxMs=0,windowAttempts=0,windowStart;
  const begin=performance.now(),deadline=begin+seconds*1000;
  windowStart=begin;
  function sample(){
    const now=performance.now();
    const entry={elapsed_s:Math.round((now-begin)/100)/10,completed,
      window_attempts:windowAttempts,window_rps:Math.round(windowAttempts*1000/Math.max(1,now-windowStart)),
      rss_mib:readRss(),pool:readPool()};
    windows.push(entry);
    console.log("IDK_SOAK_SAMPLE "+JSON.stringify({provider,runtime,...entry}));
    windowAttempts=0;windowStart=now;
  }
  const initialRss=readRss(),initialPool=readPool();
  const timer=setInterval(sample,10000);
  try{
    await Promise.all(Array.from({length:concurrency},async()=>{
      while(!firstError&&performance.now()<deadline){
        const i=issued++,fresh=i%128===0&&created.size<512,mismatch=i%128===1;
        const k=fresh?key():seeds[i%seeds.length];
        if(fresh)created.set(k,null);
        const kind=fresh?"original":mismatch?"changed":"reordered";
        const start=performance.now();
        try{
          let r;
          try{r=await request(k,kind);}
          catch(e){if(!transport(e))throw e;statuses.transport=(statuses.transport??0)+1;continue;}
          statuses[r.status]=(statuses[r.status]??0)+1;
          if(retryable(r))continue;
          if(mismatch){
            assert.equal(r.status,422,r.bytes.toString());
            assert.equal(JSON.parse(r.bytes).code,"IDEMPOTENCY_FINGERPRINT_MISMATCH");
          }else if(fresh){valid(r);created.set(k,r);}
          else same(r,seed.get(k));
        }catch(e){firstError??=e;}
        finally{
          const ms=performance.now()-start;maxMs=Math.max(maxMs,ms);
          buckets[limits.findIndex(n=>ms<=n)]++;
          completed++;windowAttempts++;
        }
      }
    }));
  }finally{clearInterval(timer);sample();}
  if(firstError)throw firstError;
  const elapsed=performance.now()-begin;
  assert.ok(elapsed>=seconds*1000,"Soak ended early");
  assert.equal(issued,completed);
  assert.ok(completed>=concurrency*10,"Insufficient traffic");
  const upper=p=>{
    const target=Math.ceil(completed*p);let n=0;
    for(let i=0;i<buckets.length;i++){n+=buckets[i];if(n>=target)return Number.isFinite(limits[i])?limits[i]:null;}
    return null;
  };
  // Do not hide unexpected timeouts under a successful replay assertion.
  assert.equal(statuses.transport??0,0,"Transport failures during steady soak");
  for(const [k,observed] of created){
    const r=await recover(k);if(observed)same(r,observed);
    assert.ok(!locations.has(r.location));locations.add(r.location);await verify(k,r);
  }
  for(const [k,original] of seed){same(await recover(k),original);await verify(k,original);}
  await sleep(5000);
  const finalPool=readPool();
  if(finalPool){
    assert.equal(finalPool.waiting??finalPool.pending??0,0,"Pool waiters did not drain");
    if("borrowed" in finalPool)assert.equal(finalPool.borrowed,0,"Pool connections did not return");
    if("idle" in finalPool)assert.equal(finalPool.idle,finalPool.total,"Pool clients did not return");
  }
  const result={provider,runtime,seconds,concurrency,elapsed_ms:Math.round(elapsed),
    attempts:completed,statuses,committed_orders:seed.size+created.size,
    latency_upper_ms:{p50:upper(.5),p95:upper(.95),p99:upper(.99),max:Math.round(maxMs*100)/100},
    rss_mib:{scope:memScope,before:initialRss,sampled_max:Math.max(0,...[initialRss,...windows.map(x=>x.rss_mib)].filter(x=>x!==null)),after_idle:readRss()},
    initial_pool:initialPool,final_pool:finalPool,samples:windows.length};
  console.log("IDK_SOAK_VERIFIED "+JSON.stringify(result));
  assert.equal(locations.size,result.committed_orders);
  return {committedOrders:result.committed_orders,report:result};
}
