/**
 * Child process for SIGKILL transaction-failure injection tests.
 * Parent supplies a hash/key and kills this process at a deliberately
 * signalled checkpoint. This is a test worker, NOT a production API.
 */
import pg from "pg";
import { PostgresTransactionalExecutor } from "@idempotencykit/postgres";

const [mode, key, fingerprint] = process.argv.slice(2);
if (!process.send ||
    !["before-commit", "after-commit"].includes(mode) ||
    !key || !fingerprint || !process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL)
  throw new Error("Transactional SIGKILL worker must run through fork with test arguments.");

const pool = new pg.Pool({
  connectionString: process.env.IDEMPOTENCYKIT_TEST_POSTGRES_URL,
  max: 2,
});
pool.on("error", error => {
  process.stderr.write("Worker database error: " + error.message + "\n");
});

function awaitParentOrKill() {
  return new Promise(resolve => {
    process.once("message", value => {
      if (value === "continue") resolve();
    });
  });
}

const executor = new PostgresTransactionalExecutor(pool, 10_000);
const result = await executor.execute(key, fingerprint, async client => {
  const saved = await client.query(
    `INSERT INTO idempotencykit_txn_orders_test(scope_key_hash, quantity)
      VALUES ($1, $2) RETURNING id`, [key, 42]);
  const id = saved.rows[0].id;
  if (mode === "before-commit") {
    process.send({ phase: "before-commit", id });
    // Hold the still-uncommitted database transaction open until SIGKILL.
    await awaitParentOrKill();
  }
  return {
    statusCode: 201,
    contentType: "application/json",
    body: Buffer.from(JSON.stringify({ id, quantity: 42 })),
    headers: { Location: "/orders/" + id },
  };
}, 86_400_000);

if (mode === "after-commit") {
  // execute() returns ONLY after the DB acknowledged COMMIT.
  // A separate worker can now die before it responds over HTTP.
  process.send({ phase: "after-commit", status: result.status });
  await awaitParentOrKill();
}

await pool.end();
