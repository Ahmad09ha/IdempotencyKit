
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { readFileSync } from "node:fs";
import { performance } from "node:perf_hooks";
import { setTimeout as sleep } from "node:timers/promises";
import { availableParallelism, platform, arch } from "node:os";

// Small bounded, closed-loop HTTP baseline. Only disposable fixtures may call it.
// No performance pass/fail threshold: shared CI runners are noisy.
export function readRssMiB(pid) {
  try {
    const match = /^VmRSS:\s+(\d+)\s+kB/m.exec(readFileSync("/proc/" + pid + "/status", "utf8"));
    return match ? Number(match[1]) / 1024 : null;
  } catch { return null; }
}
const rounded = n => Math.round(n * 100) / 100;
const percentile = (sorted,p) => rounded(sorted[Math.max(0,Math.ceil(sorted.length*p)-1)]);
const key = () => "perf-" + randomUUID();
function retryable(r,provider) {
  if (![409,503].includes(r.status)) return false;
  const code = JSON.parse(r.bytes).code;
  return (r.status===409 && code==="IDEMPOTENCY_IN_PROGRESS") ||
    (provider==="sqlite" && r.status===503 && code==="IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
}
function assertCreated(r) {
  assert.equal(r.status,201,r.bytes.toString());
  assert.ok(r.location);
  assert.equal(JSON.parse(r.bytes).quantity,2);
}
function same(a,b) {
  assertCreated(a); assertCreated(b);
  assert.equal(a.location,b.location);
  assert.deepEqual(a.bytes,b.bytes);
}

export async function runHttpBaseline({provider,runtime,request,verify,readRss,memScope}) {
  const count = 96, concurrency = 8, warmup = 12;
  const results = [];
  async function recover(k,alternate=false) {
    for(let n=0;n<20;n++) {
      const r=await request(k,alternate);
      if(r.status===201){assertCreated(r);return r;}
      assert.ok(retryable(r,provider),r.bytes.toString());
      await sleep(100);
    }
    assert.fail("Same-key recovery did not complete");
  }
  async function measure(mode,keys,alternate=false) {
    const samples = new Array(keys.length), durations = new Array(keys.length);
    let cursor=0;
    const statuses = Object.create(null);
    const rssBefore=readRss();
    let sampledMax=rssBefore;
    const began=performance.now();
    await Promise.all(Array.from({length:concurrency},async()=>{
      while(true){
        const i=cursor++; if(i>=keys.length)return;
        const start=performance.now();
        const r=await request(keys[i],alternate);
        durations[i]=performance.now()-start;
        samples[i]=r;
        statuses[r.status]=(statuses[r.status]??0)+1;
        if(r.status===201)assertCreated(r);
        else assert.ok(retryable(r,provider),r.status+" "+r.bytes.toString());
        // Sampling is sparse and not a high-water mark.
        if(i%8===0){const v=readRss();if(v!==null)sampledMax=Math.max(sampledMax??0,v);}
      }
    }));
    const elapsed=performance.now()-began, rssAfter=readRss();
    if(rssAfter!==null)sampledMax=Math.max(sampledMax??0,rssAfter);
    const sorted=[...durations].sort((a,b)=>a-b);
    const report={provider,runtime,mode,requests:keys.length,concurrency,statuses,
      elapsed_ms:rounded(elapsed),requests_per_second:rounded(keys.length*1000/elapsed),
      latency_ms:{p50:percentile(sorted,0.5),p95:percentile(sorted,0.95),p99:percentile(sorted,0.99)},
      rss_mib:{before:rssBefore===null?null:rounded(rssBefore),
        sampled_max:sampledMax===null?null:rounded(sampledMax),
        after:rssAfter===null?null:rounded(rssAfter),scope:memScope},
      environment:{platform:platform(),arch:arch(),logical_cpus:availableParallelism(),load_client_node:process.version}};
    console.log("IDK_BENCHMARK "+JSON.stringify(report));
    results.push(report);
    return samples;
  }
  for(let i=0;i<warmup;i++){const k=key();await verify(k,await recover(k));}
  const fresh=Array.from({length:count},key);
  const first=await measure("fresh",fresh);
  const saved=new Map();
  for(let i=0;i<count;i++){
    const r=first[i].status===201?first[i]:await recover(fresh[i]);
    saved.set(fresh[i],r); await verify(fresh[i],r);
  }
  assert.equal(new Set([...saved.values()].map(r=>r.location)).size,count);
  const replay=await measure("replay",fresh,true);
  for(let i=0;i<count;i++){
    const r=replay[i].status===201?replay[i]:await recover(fresh[i],true);
    same(r,saved.get(fresh[i])); await verify(fresh[i],r);
  }
  const hot=key(), hotSamples=await measure("cold-hot-key",Array(count).fill(hot),true);
  const original=await recover(hot);
  for(const r of hotSamples)if(r.status===201)same(r,original);
  await verify(hot,original);
  return {reports:results,committedOrders:warmup+count+1};
}
