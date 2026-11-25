import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { openSync,closeSync,writeSync,fsyncSync,unlinkSync,statfsSync,realpathSync } from "node:fs";
import { resolve,join } from "node:path";
import { DatabaseSync } from "node:sqlite";

// Only a dedicated, tiny, disposable ext4 loop-mounted CI image is allowed.
assert.equal(process.env.GITHUB_ACTIONS,"true");
assert.equal(process.env.IDEMPOTENCYKIT_ISOLATED_FULL,"1");
const [runtime,portText,mountArg,dbArg]=process.argv.slice(2);
assert.ok(["dotnet","node"].includes(runtime));
const port=Number(portText),mount=realpathSync(mountArg),dbFile=realpathSync(dbArg);
assert.ok(Number.isInteger(port)&&port>0&&port<=65535);
assert.ok(mount.startsWith("/tmp/idk-full.")&&mount.endsWith("/mount"));
assert.ok(dbFile.startsWith(mount+"/"));
const fs=statfsSync(mount);
assert.equal(fs.type,0xef53,"Expected isolated ext filesystem");
assert.ok(fs.blocks*fs.bsize<=40*1024*1024,"Refusing to fill a non-fixture filesystem");
assert.notEqual(statfsSync(resolve(mount,"..")).blocks,fs.blocks,"Must be a separate filesystem");
const filler=join(mount,"fixture-fill-"+randomUUID()),probe=join(mount,"fixture-probe-"+randomUUID());
const base="http://127.0.0.1:"+port,auth={Authorization:"Bearer orders-demo"};
async function post(key,changed=false){
  const r=await fetch(base+"/orders",{method:"POST",signal:AbortSignal.timeout(15000),
    headers:{...auth,"Content-Type":"application/json","Idempotency-Key":key},
    body:changed?'{"quantity":3,"productId":50}':'{"productId":50,"quantity":2}'});
  return {status:r.status,location:r.headers.get("location"),bytes:Buffer.from(await r.arrayBuffer())};
}
function count(){
  const db=new DatabaseSync(dbFile,{readOnly:true});
  try{
    const orders=db.prepare("SELECT COUNT(*) AS n FROM orders").get().n;
    const records=db.prepare("SELECT COUNT(*) AS n FROM idempotencykit_records WHERE state='completed'").get().n;
    const total=db.prepare("SELECT COUNT(*) AS n FROM idempotencykit_records").get().n;
    assert.equal(records,total);return [orders,records];
  }finally{db.close();}
}
function unavailable(r){
  assert.equal(r.status,503,r.bytes.toString());
  assert.equal(JSON.parse(r.bytes).code,"IDEMPOTENCY_TRANSACTION_UNAVAILABLE");
}
const before=count(),old="full-old-"+randomUUID(),fresh="full-new-"+randomUUID();
const original=await post(old);
assert.equal(original.status,201,original.bytes.toString());
assert.deepEqual(count(),before.map(n=>n+1));
let fd,proofFd,exhausted=false,written=0;
try{
  fd=openSync(filler,"wx");
  const bytes=Buffer.alloc(64*1024,0xa5);
  // Bounded actual allocation, not sparse truncation or page-limit simulation.
  while(written<40*1024*1024){
    try{const n=writeSync(fd,bytes);assert.ok(n>0);written+=n;fsyncSync(fd);}
    catch(e){if(e.code!=="ENOSPC")throw e;exhausted=true;break;}
  }
  assert.ok(exhausted,"No real OS ENOSPC");
  // A distinct file/operation proves the space failure applies to the volume.
  try{
    proofFd=openSync(probe,"wx");
    writeSync(proofFd,Buffer.alloc(4096,0xb6));fsyncSync(proofFd);
    assert.fail("Expected ENOSPC on independent allocation");
  }catch(e){assert.equal(e.code,"ENOSPC");}
  finally{if(proofFd!==undefined){closeSync(proofFd);proofFd=undefined;}}
  unavailable(await post(fresh));
  const replay=await post(old);
  if(replay.status===201)assert.deepEqual(replay,original);else unavailable(replay);
  console.log("IDK_ENOSPC "+JSON.stringify({runtime,allocated_bytes:written,free_blocks:statfsSync(mount).bavail,new_request:503,old_request:replay.status}));
}finally{
  if(fd!==undefined)closeSync(fd);
  for(const file of [filler,probe]){try{unlinkSync(file);}catch(e){if(e.code!=="ENOENT")throw e;}}
}
assert.ok(statfsSync(mount).bavail>0);
assert.deepEqual(count(),before.map(n=>n+1),"No partial write after failed request");
assert.deepEqual(await post(old),original);
const recovered=await post(fresh);
assert.equal(recovered.status,201,recovered.bytes.toString());
assert.notEqual(recovered.location,original.location);
assert.deepEqual(await post(fresh),recovered);
assert.equal((await post(fresh,true)).status,422);
assert.deepEqual(count(),before.map(n=>n+2));
console.log("IDK_ENOSPC_RECOVERY "+JSON.stringify({runtime,one_new_order:true,original_replay:true}));
