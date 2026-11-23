import test from "node:test";
import assert from "node:assert/strict";
import { IdempotencyRecordStates, isCompletedState } from "../packages/core/dist/index.js";

test("only completed record state is marked completed", () => {
  assert.equal(isCompletedState(IdempotencyRecordStates.Processing), false);
  assert.equal(isCompletedState(IdempotencyRecordStates.Completed), true);
  assert.equal(isCompletedState(IdempotencyRecordStates.Uncertain), false);
});
