import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync,rmSync,readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { initializeSqliteDatabase,SqliteIdempotencyStore } from "@idempotencykit/sqlite";
import { verifyOperationalReport } from "./operational-report-fixture.mjs";

test("SQLite operational report is read-only and classifies actual persisted records",async()=>{
  const dir=mkdtempSync(join(tmpdir(),"idk-ops-report-")),file=join(dir,"fixture.db");
  try{
    initializeSqliteDatabase(file);
    const query=readFileSync(new URL("../../../ops/sql/sqlite/record-health.sql",import.meta.url),"utf8");
    const store=new SqliteIdempotencyStore(file);
    await verifyOperationalReport({provider:"sqlite",store,
      runReport(){
        const db=new DatabaseSync(file,{readOnly:true});
        try{return db.prepare(query).all();}finally{db.close();}
      },
      expire(key,bucket){
        const db=new DatabaseSync(file);
        const now="CAST(unixepoch('subsec')*1000 AS INTEGER)";
        const clause=bucket==="processing_expired"?
          "created_at="+now+"-120000,lease_expires_at="+now+"-60000":
          "created_at="+now+"-180000,completed_at="+now+"-120000,expires_at="+now+"-60000";
        try{assert.equal(db.prepare("UPDATE idempotencykit_records SET "+clause+" WHERE scope_key_hash=?").run(key).changes,1);}
        finally{db.close();}
      },
      readStates(keys){
        const db=new DatabaseSync(file,{readOnly:true});
        try{return keys.map(k=>db.prepare("SELECT state FROM idempotencykit_records WHERE scope_key_hash=?").get(k)?.state);}
        finally{db.close();}
      },
    });
  }finally{rmSync(dir,{recursive:true,force:true});}
});
