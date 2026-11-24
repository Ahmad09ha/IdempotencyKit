import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { performance } from "node:perf_hooks";
import { setTimeout as sleep } from "node:timers/promises";

// Dedicated disposable fixtures only. Bounded closed-loop load, not a capacity claim.
const newKey=()=> "stress-"+randomUUID();
const round=n=>Math.round(n*100)/100;
const quantile=(sorted,p)=>round(sorted[Math.max(0,Math.ceil(sorted.length*p)-1)]);
function problem(r,code){assert.equal(JSON.parse(r.bytes).code,code);}
function same(r,original){
  assert.equal(r.status,201,r.bytes.toString());
  assert.equal(r.location,original.location);
  assert.deepEqual(r.bytes,original.bytes);
}
function validateCreated(r){
  assert.equal(r.status,201,r.bytes.toString());
  assert.ok(r.location);
  assert.equal(JSON.parse(r.bytes).quantity,2);
}
function transient(r){
  if(r.status===409){problem(r,"IDEMPOTENCY_IN_PROGRESS");return true;}
  if(r.status===503){problem(r,"IDEMPOTENCY_TRANSACTION_UNAVAILABLE");return true;}
  return false;
}
function transport(e){
  return e?.name==="TimeoutError" || e?.name==="AbortError" ||
    (e instanceof TypeError && e.message==="fetch failed");
}
export async function runSustainedHttpStress({provider,runtime,request,verify,readRss,memScope,readPool=()=>null}){
  const seed=new Map(), results=[];
  let committed=0;
  async function recover(key){
    const deadline=performance.now()+60000;
    for(let attempt=0;attempt<30&&performance.now()<deadline;attempt++){
      try{
        const r=await request(key,"original");
        if(r.status===201){validateCreated(r);return r;}
        assert.ok(transient(r),"Unexpected recovery response "+r.status+" "+r.bytes.toString());
      }catch(e){if(!transport(e))throw e;}
      await sleep(Math.min(100+attempt*100,1000));
    }
    assert.fail("Bounded same-key recovery exhausted");
  }
  // These are real, unique, already committed records; all replays must match.
  for(let i=0;i<24;i++){
    const k=newKey(),r=await recover(k);
    await verify(k,r);
    seed.set(k,r);committed++;
  }
  const keys=[...seed.keys()];
  const allLocations=new Set([...seed.values()].map(r=>r.location));
  assert.equal(allLocations.size,seed.size);
  for(const concurrency of [8,32,128]){
    const seconds=12, cap=16000, statuses={}, kinds={}, latencies=[];
    const newRecords=new Map();
    const rss=[], pools=[];
    let issued=0, firstError;
    const started=performance.now(), deadline=started+seconds*1000;
    function sample(){
      const v=readRss();if(v!==null)rss.push(v);
      const p=readPool();if(p!==null)pools.push(p);
    }
    sample();const timer=setInterval(sample,1000);
    try{
      await Promise.all(Array.from({length:concurrency},async()=>{
        while(!firstError&&performance.now()<deadline&&issued<cap){
          const index=issued++, fresh=index%16===0, mismatch=index%16===1;
          const key=fresh?newKey():keys[index%4===2?0:index%keys.length];
          const kind=fresh?"original":mismatch?"changed":"reordered";
          kinds[kind]=(kinds[kind]??0)+1;
          if(fresh)newRecords.set(key,null);
          const begin=performance.now();
          let r;
          try{
            try{r=await request(key,kind);}
            catch(e){
              if(!transport(e))throw e;
              statuses.transport=(statuses.transport??0)+1;
              continue;
            }
            statuses[r.status]=(statuses[r.status]??0)+1;
            if(transient(r))continue;
            if(mismatch){
              assert.equal(r.status,422,r.bytes.toString());
              problem(r,"IDEMPOTENCY_FINGERPRINT_MISMATCH");
            }else if(fresh){
              validateCreated(r);
              newRecords.set(key,r);
            }else same(r,seed.get(key));
          }catch(e){firstError??=e;}
          finally{latencies.push(performance.now()-begin);}
        }
      }));
    }finally{clearInterval(timer);sample();}
    if(firstError)throw firstError;
    assert.equal(latencies.length,issued);
    assert.ok(issued>=concurrency,"Too few samples");
    const elapsed=performance.now()-started;
    const sorted=latencies.sort((a,b)=>a-b);
    const report={provider,runtime,concurrency,configured_seconds:seconds,attempt_cap:cap,
      attempts:issued,elapsed_ms:round(elapsed),attempts_per_second:round(issued*1000/elapsed),
      statuses,kinds,latency_ms:{p50:quantile(sorted,.5),p95:quantile(sorted,.95),p99:quantile(sorted,.99),max:round(sorted.at(-1))},
      rss_mib:{scope:memScope,before:rss.length?round(rss[0]):null,
        sampled_max:rss.length?round(Math.max(...rss)):null,after:rss.length?round(rss.at(-1)):null,samples:rss.length},
      pool_samples:pools};
    console.log("IDK_STRESS "+JSON.stringify(report));
    results.push(report);
    // Recovery and readback are outside measured windows. Never mint a new key
    // after a failed/ambiguous attempt. Every fresh key has exactly one effect.
    for(const [key,observed] of newRecords){
      const stored=await recover(key);
      if(observed)same(stored,observed);
      assert.ok(!allLocations.has(stored.location),"Different keys shared one order");
      allLocations.add(stored.location);
      await verify(key,stored);committed++;
    }
    // Recheck original rows after mismatch traffic and pool pressure.
    for(const [key,original] of seed){
      same(await recover(key),original);
      await verify(key,original);
    }
  }
  // Recovery is a correctness assertion, not a claim that all timed attempts succeeded.
  assert.equal(allLocations.size,committed);
  console.log("IDK_STRESS_VERIFIED "+JSON.stringify({provider,runtime,committed_orders:committed}));
  return {committedOrders:committed,reports:results};
}
