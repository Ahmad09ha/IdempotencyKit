import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { computeScopeKey,computeRequestFingerprint } from "@idempotencykit/core";

const buckets=["processing_active","processing_expired","uncertain","completed_retained","completed_expired"];
function parse(rows){
  const result=new Map();
  for(const row of rows){
    assert.deepEqual(Object.keys(row).sort(),["bucket","oldest_age_seconds","record_count"]);
    assert.ok([...buckets,"invalid_state"].includes(row.bucket));
    assert.ok(!result.has(row.bucket));
    const count=Number(row.record_count),age=Number(row.oldest_age_seconds);
    assert.ok(Number.isSafeInteger(count)&&count>0);
    assert.ok(Number.isFinite(age)&&age>=0);
    result.set(row.bucket,{count,age});
  }
  return result;
}
export async function verifyOperationalReport({provider,store,runReport,expire,readStates}){
  const before=parse(await runReport());
  const fixtureKeys=[];
  const fingerprint=computeRequestFingerprint({method:"POST",operationId:"health-fixture",path:"/health-fixture",bodyJson:'{"fixture":true}'});
  const response={statusCode:201,contentType:"application/json",body:Buffer.from('{"fixture":true}'),headers:{}};
  for(const bucket of buckets){
    const key=computeScopeKey({namespace:"ops-health-fixture",operationId:"health-fixture",principalId:"fixture",clientKey:randomUUID()});
    fixtureKeys.push(key);
    const claim=await store.tryClaim(key,fingerprint,600000);
    assert.equal(claim.status,"acquired");
    assert.ok(claim.ownerToken);
    if(bucket==="uncertain")assert.equal(await store.tryMarkUncertain(key,fingerprint,claim.ownerToken),true);
    if(bucket.startsWith("completed_"))assert.equal(await store.tryComplete(key,fingerprint,claim.ownerToken,response,3600000),true);
    if(bucket.endsWith("_expired"))await expire(key,bucket);
  }
  const statesBefore=await readStates(fixtureKeys);
  assert.deepEqual(statesBefore,["processing","processing","uncertain","completed","completed"]);
  const after=parse(await runReport());
  const again=parse(await runReport());
  for(const bucket of buckets){
    const count=(before.get(bucket)?.count??0)+1;
    assert.equal(after.get(bucket)?.count,count,bucket);
    assert.equal(again.get(bucket)?.count,count,bucket);
  }
  assert.ok(after.get("completed_expired").age>=60);
  assert.ok(after.get("processing_expired").age>=60);
  assert.equal(after.get("invalid_state")?.count??0,before.get("invalid_state")?.count??0);
  assert.deepEqual(await readStates(fixtureKeys),statesBefore,"Report must not mutate even expired claims");
  // No scoped hashes, principal IDs, response fields or raw request data in output.
  console.log("IDK_OPERATIONS_REPORT_VERIFIED "+JSON.stringify({provider,buckets:buckets.length,read_only:true}));
}
