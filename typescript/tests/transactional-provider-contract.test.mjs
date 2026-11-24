import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  TransactionalExecutionStatuses,
} from "@idempotencykit/core";

// The same JSON fixture is validated by the .NET core tests.
// Real database tests enforce behavior separately for each provider.
const fixture = JSON.parse(readFileSync(new URL(
  "../../spec/fixtures/transactional-provider-v1.json",
  import.meta.url), "utf8"));

test("provider-neutral outcome names agree with .NET and the v1 fixture", () => {
  assert.equal(fixture.version, 1);
  const names = Object.keys(TransactionalExecutionStatuses);
  assert.equal(fixture.statuses.length, names.length);
  assert.deepEqual(
    Object.keys(TransactionalExecutionStatuses).sort(),
    fixture.statuses.map(row => row.dotnet).sort());
  assert.deepEqual(
    Object.values(TransactionalExecutionStatuses).sort(),
    fixture.statuses.map(row => row.typescript).sort());
  for (const row of fixture.statuses) {
    assert.equal(TransactionalExecutionStatuses[row.dotnet], row.typescript);
    assert.equal(row.response, ["executed", "replayed"].includes(row.typescript));
    assert.equal(row.callback, row.typescript === "executed" ? "once" : "never");
  }
});

test("provider-neutral contract explicitly requires one-transaction discipline", () => {
  for (const key of [
    "businessAndResponseOneTransaction",
    "driverSessionProvidedToCallback",
    "validateResponseBeforeCommit",
    "noAutomaticCallbackRetryOnAmbiguousCommit",
    "completedReplayRequiresFreshAuthorization",
    "sqlDialectAndConcurrencyProviderSpecific",
  ]) {
    assert.equal(fixture.invariants[key], true, key);
  }
});
