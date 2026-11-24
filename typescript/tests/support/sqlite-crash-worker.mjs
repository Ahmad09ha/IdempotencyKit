/**
 * Genuine isolated SQLite PROCESS for before/after COMMIT SIGKILL and
 * unrelated-file-writer contention. This worker intentionally never sends
 * a simulated successful HTTP response until after the parent's crash test.
 */
import { DatabaseSync } from "node:sqlite";
import { SqliteTransactionalExecutor } from "@idempotencykit/sqlite";

const [mode, path, key, fingerprint] = process.argv.slice(2);
if (!["before-commit", "after-commit", "unrelated-writer"].includes(mode) ||
    !process.send)
  throw new Error("SQLite worker must be forked with IPC and a known mode.");

const never = () => new Promise(() => {});

if (mode === "unrelated-writer") {
  const db = new DatabaseSync(path, { timeout: 1000 });
  db.exec("BEGIN IMMEDIATE");
  db.prepare(
    "INSERT INTO sqlite_orders_test(scope_key_hash,quantity) VALUES (?,?)",
  ).run("unrelated-" + process.pid, 100);
  process.send({ phase: "locked" });
  await never();
} else {
  const executor = new SqliteTransactionalExecutor(path, 200);
  const result = await executor.execute(key, fingerprint, async session => {
    const inserted = session.prepare(
      "INSERT INTO sqlite_orders_test(scope_key_hash,quantity) VALUES (?,?)",
    ).run(key, 7);
    const id = Number(inserted.lastInsertRowid);
    if (mode === "before-commit") {
      process.send({ phase: "before-commit" });
      await never(); // BEGIN IMMEDIATE still holds domain INSERT + claim.
    }
    return {
      statusCode: 201, contentType: "application/json",
      body: Buffer.from(JSON.stringify({ id, quantity: 7 })),
      headers: { Location: "/orders/" + id },
    };
  }, 86_400_000);

  if (result.status !== "executed")
    throw new Error("Crash worker did not execute a new SQLite operation.");
  process.send({ phase: "after-commit", response: Buffer.from(result.response.body).toString("utf8") });
  await never(); // COMMIT acknowledged, but no simulated HTTP response sent.
}
