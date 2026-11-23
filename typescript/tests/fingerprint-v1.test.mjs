import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import {
  computeRequestFingerprint, computeScopeKey,
} from "../packages/core/dist/index.js";

const fixtures = JSON.parse(readFileSync(fileURLToPath(
  new URL("../../spec/test-vectors/fingerprint-v1.json", import.meta.url)
), "utf8"));

test("all v1 scope identity golden vectors", () => {
  assert.equal(fixtures.schemaVersion, 1);
  for (const item of fixtures.scopeCases) {
    assert.equal(computeScopeKey(item.scope), item.expected, item.name);
  }
});

test("all v1 JSON request fingerprint golden vectors", () => {
  for (const item of fixtures.fingerprintCases) {
    assert.equal(computeRequestFingerprint(item.request), item.expected, item.name);
  }
});

test("invalid keys/JSON are rejected rather than silently normalized", () => {
  for (const item of fixtures.invalidCases) {
    assert.throws(() => item.scope
      ? computeScopeKey(item.scope)
      : computeRequestFingerprint(item.request), undefined, item.name);
  }
});

test("matching and different intended operations are distinguished", () => {
  const lookup = Object.fromEntries(fixtures.fingerprintCases.map(
    item => [item.name, item.expected]
  ));
  assert.equal(lookup["basic-order"], lookup["reordered-fields"]);
  assert.equal(lookup["query-order-a"], lookup["query-order-b"]);
  assert.notEqual(lookup["basic-order"], lookup["changed-quantity"]);
  assert.notEqual(lookup["no-body"], lookup["json-null"]);
  assert.notEqual(lookup["array-order-a"], lookup["array-order-b"]);
});
