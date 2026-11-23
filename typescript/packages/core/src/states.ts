import type { IdempotencyRecordState } from "./contracts.js";

/** Proposed record lifecycle. Storage, state transitions and protection are not implemented. */
export const IdempotencyRecordStates = {
  Processing: "processing",
  Completed: "completed",
  Uncertain: "uncertain",
} as const;

/** A completed record is only a replay candidate when scope, fingerprint and auth also match. */
export function isCompletedState(state: IdempotencyRecordState): boolean {
  return state === IdempotencyRecordStates.Completed;
}
