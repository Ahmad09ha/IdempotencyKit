import { performance } from "node:perf_hooks";
import type { IdempotencyClaimResult } from "./idempotency-store.js";
import type { TransactionalIdempotencyResult } from "./transactional-executor.js";

export type IdempotencyTelemetryStage =
  | "standard.claim" | "standard.execute" | "transactional.execute";
export type IdempotencyTelemetryOutcome =
  | "acquired" | "completed" | "executed" | "replayed" | "in_progress"
  | "fingerprint_mismatch" | "uncertain" | "unconfirmed" | "error";

/** Bounded event: contains no key, caller, route, body, response or error details. */
export interface IdempotencyTelemetryEvent {
  readonly stage: IdempotencyTelemetryStage;
  readonly outcome: IdempotencyTelemetryOutcome;
  readonly durationMs: number;
}

const observers = new Set<(event: IdempotencyTelemetryEvent) => void | Promise<void>>();

/** Register a process-local observer and get back its unsubscribe function. */
export function subscribeIdempotencyTelemetry(
  listener: (event: IdempotencyTelemetryEvent) => void | Promise<void>,
): () => void {
  if (typeof listener !== "function") throw new TypeError("A telemetry listener is required.");
  const observer = (event: IdempotencyTelemetryEvent) => listener(event);
  observers.add(observer);
  return () => { observers.delete(observer); };
}

function publish(stage: IdempotencyTelemetryStage, outcome: IdempotencyTelemetryOutcome,
  startedAt: number): void {
  if (!observers.size) return;
  const event = Object.freeze({ stage, outcome, durationMs: performance.now() - startedAt });
  for (const observer of [...observers]) {
    try {
      const pending = observer(event);
      if (pending) void Promise.resolve(pending).catch(() => {});
    } catch { /* A collector failure must not change execution or replay. */ }
  }
}

async function observe<T>(
  operation: () => Promise<T>,
  stage: IdempotencyTelemetryStage,
  classify: (result: T) => IdempotencyTelemetryOutcome,
): Promise<T> {
  const start = performance.now();
  let outcome: IdempotencyTelemetryOutcome = "error";
  try {
    const result = await operation();
    outcome = classify(result);
    return result;
  } finally {
    publish(stage, outcome, start);
  }
}

/** Observe Standard-mode store claim duration and coordination outcome. */
export function observeIdempotencyClaim(claim: () => Promise<IdempotencyClaimResult>):
  Promise<IdempotencyClaimResult> {
  return observe(claim, "standard.claim", result => {
    switch (result?.status) {
      case "acquired": return result.ownerToken ? "acquired" : "error";
      case "completed": return result.response ? "replayed" : "error";
      case "in_progress": return "in_progress";
      case "fingerprint_mismatch": return "fingerprint_mismatch";
      case "uncertain": return "uncertain";
      default: return "error";
    }
  });
}

/** Observe a provider's Transactional-mode execution duration and outcome. */
export function observeIdempotencyTransaction(execute: () => Promise<TransactionalIdempotencyResult>):
  Promise<TransactionalIdempotencyResult> {
  return observe(execute, "transactional.execute", result => {
    switch (result?.status) {
      case "executed": return result.response ? "executed" : "error";
      case "replayed": return result.response ? "replayed" : "error";
      case "in_progress": return "in_progress";
      case "fingerprint_mismatch": return "fingerprint_mismatch";
      case "uncertain": return "uncertain";
      default: return "error";
    }
  });
}

/** Observe a newly claimed Standard-mode handler's persistence outcome. */
export function recordIdempotencyStandardExecution(startedAt: number, persisted: boolean): void {
  publish("standard.execute", persisted ? "completed" : "unconfirmed", startedAt);
}
