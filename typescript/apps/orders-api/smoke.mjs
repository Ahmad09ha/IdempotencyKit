// Exercise either real Orders sample server through HTTP, not mocks.
// Usage: node typescript/apps/orders-api/smoke.mjs http://127.0.0.1:5050
import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";

const server = process.argv[2] ?? "http://127.0.0.1:3000";
const auth = { Authorization: "Bearer orders-demo" };
const key = randomUUID();

async function request(path, options) {
  const response = await fetch(new URL(path, server), options);
  return { status: response.status, headers: response.headers, body: await response.text() };
}

async function countOrders() {
  const response = await request("/orders", { headers: auth });
  assert.equal(response.status, 200);
  return JSON.parse(response.body).count;
}

function create(body, authorized = true) {
  return request("/orders", {
    method: "POST",
    headers: {
      ...(authorized && auth),
      "Content-Type": "application/json",
      "Idempotency-Key": key,
    },
    body,
  });
}

const before = await countOrders();
const bad = await create('{"productId":42,"quantity":0}');
assert.equal(bad.status, 400);
assert.equal(await countOrders(), before);

const original = await create('{"productId":42,"quantity":2}');
assert.equal(original.status, 201);
const order = JSON.parse(original.body);
assert.equal(order.productId, 42);
assert.equal(order.quantity, 2);
assert(Number.isSafeInteger(order.id) && order.id > 0);
assert.equal(original.headers.get("location"), `/orders/${order.id}`);

const retry = await create('{ "quantity": 2, "productId": 42 }');
assert.equal(retry.status, original.status);
assert.equal(retry.headers.get("location"), original.headers.get("location"));
assert.equal(retry.body, original.body);

const mismatch = await create('{"productId":42,"quantity":3}');
assert.equal(mismatch.status, 422);
assert.equal(JSON.parse(mismatch.body).code, "IDEMPOTENCY_FINGERPRINT_MISMATCH");
assert.equal((await create('{"productId":42,"quantity":2}', false)).status, 401);
assert.equal(await countOrders(), before + 1);

const stored = await request(original.headers.get("location"), { headers: auth });
assert.equal(stored.status, 200);
assert.deepEqual(JSON.parse(stored.body), order);
console.log(`Orders demo passed: only order ${order.id} was committed; replay was byte-identical.`);
