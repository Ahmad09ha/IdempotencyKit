# IdempotencyKit operations

## Retry and retention settings

The client generates one key per intended operation and keeps that key across retries. Document how long it may retry, including delayed jobs and network outages. Set the completed-record retention window longer than this horizon with a margin for deployment and clock differences. The HTTP adapters default to 24 hours (`IdempotencyHttpOptions.Retention` in .NET and `retentionMs` in NestJS). Changing the setting affects newly completed records; it does not rewrite old expiry timestamps.

Standard mode has a separate 30-second default claim lease. A lease coordinates who can finish recording a response; it does not prove a slow handler stopped, or that a timed-out handler rolled back. Size the lease against protected handler and persistence latency. Do not treat lease extension as business idempotency. Where a duplicate business effect remains unacceptable even after replay expiry, enforce domain-level uniqueness as well (for example, a unique order reference).

Every provider supplies bounded cleanup on its Standard store: `PurgeExpiredCompletedAsync(batchSize, cancellationToken)` in .NET or `purgeExpiredCompleted(batchSize)` in Node. The same record table contains completed results for both modes. Have the **application** schedule cleanup; the library does not schedule it. Start with one worker per database, a small batch (for example, 100), a bounded run budget, and backoff on storage errors. Monitor rows deleted, backlog and duration. PostgreSQL skips locked rows during cleanup; a short batch is not a proof that no more records are eligible. SQLite cleanup itself is a writer and can contend with requests; avoid aggressive jobs and keep the database on a supported local filesystem.

Cleanup only deletes **expired completed** rows according to database time. A result may still replay after its expiry and before cleanup. After removal, the same key can be accepted again. **Do not auto-delete processing or uncertain rows, and do not clean the whole table to fix an outage.** Restrict table access and protect replay bytes in storage and backups; opt out of retaining sensitive responses the application cannot protect appropriately.

## When a protected request fails

| Observation | Recommended action |
| --- | --- |
| `409 IDEMPOTENCY_IN_PROGRESS` | Back off, respect `Retry-After` when present, and retry the original request with the same key. A conflict is not a reason to generate a new key. |
| `422 IDEMPOTENCY_FINGERPRINT_MISMATCH` | Stop; check for unintended key reuse or a request/normalization change. Do not automatically retry a materially different write. |
| `409 IDEMPOTENCY_OUTCOME_UNCERTAIN` | Stop automated retries and reconcile as described below. |
| `503 IDEMPOTENCY_STORAGE_UNAVAILABLE` | Keep protected routes fail-closed while restoring storage. For an established retryable caller operation, preserve its key and payload. |
| `503 IDEMPOTENCY_EXECUTION_UNCERTAIN` (Standard) | Treat the business outcome as potentially committed. Reconcile; never assume it is safe to rerun. |
| `503 IDEMPOTENCY_TRANSACTION_UNAVAILABLE` (Transactional) | A same-key, same-payload retry against the authoritative database can resolve an ambiguous COMMIT. Apply bounded backoff and investigate recurrent errors. No guarantee covers work done outside the provided transaction. |

### Standard-mode incident reconciliation

1. Preserve the existing record. Use the application's authorized business reference or incident ID; do not paste request bodies, auth material or keys into logs or support messages. Before deciding anything, ensure an earlier writer is no longer capable of committing.
2. Check the authoritative business table/ledger and any downstream side-effect system. A missing idempotency result, expired lease, failed HTTP response, restored stale replica, or absent application log does **not** prove that no work occurred.
3. If committed, communicate the authoritative result through the application's normal authorized lookup path; do not synthesize a cached HTTP response. If conclusively not committed and external effects are ruled out, the business owner may authorize a new intended attempt under the application's domain uniqueness rules. If evidence is inconclusive, leave it blocked and escalate.
4. Audit the operator's decision and source of evidence in the application incident system. This version intentionally has no generic reset or mark-as-success API for uncertain rows. Do not edit database rows by hand as a normal operational procedure.

Back up business data and idempotency rows consistently. Test restores before relying on them. A restored database may have lost writes after its restore point; do not resume protected traffic until the owner understands which effects or external operations are missing from the restored ledger. Keep the request fingerprint contract and supported response format compatible during a rolling release; otherwise drain affected routes or deliberately version the operation boundary.

## Privacy-safe adapter telemetry

Both HTTP modes report fixed stage names, fixed outcome names and elapsed duration. The library sends no data to a collector by default. **There are no request labels**: no raw or hashed keys, users or tenants, path, headers, payloads, replay bodies, owner tokens or exception text. The emitted stages measure outcomes of store claims and persistence/executor calls, **not** HTTP delivery, SQL state distributions or whether an external effect occurred. Auth/validation that rejects a request before storage does not produce events. Direct store/transaction executor calls and cleanup jobs need their own app instrumentation.

| Stage | Outcomes |
| --- | --- |
| `standard.claim` | `acquired`, `replayed`, `in_progress`, `fingerprint_mismatch`, `uncertain`, `error` |
| `standard.execute` | `completed` (a replay record was saved), `unconfirmed` (not verified, including a handler failure or lost ownership) |
| `transactional.execute` | `executed`, `replayed`, `in_progress`, `fingerprint_mismatch`, `uncertain`, `error` |

.NET uses the `IdempotencyKit` Meter (`IdempotencyTelemetry.MeterName`). Add it to the application's existing metrics setup. The counter is `idempotencykit.stage.count`; the histogram `idempotencykit.stage.duration` uses milliseconds. Both tag measurements with `idempotency.stage` and `idempotency.outcome`. .NET claim and transaction stages also report `canceled` for `OperationCanceledException`.

Node consumers can register once at startup:

```typescript
import { subscribeIdempotencyTelemetry } from "@idempotencykit/core";

const unsubscribe = subscribeIdempotencyTelemetry(({ stage, outcome, durationMs }) => {
  metrics.count("idempotencykit.stage.count", 1, { stage, outcome });
  metrics.record("idempotencykit.stage.duration", durationMs, { stage, outcome });
});
// Unsubscribe on shutdown (or when an integration test finishes).
```

Here `metrics` represents your application's existing metrics implementation. The callback is process-local and receives a frozen event; callbacks are not awaited, and thrown/rejected callback errors cannot change the idempotency result. Keep collectors lightweight and nonblocking; aggregate/export outside the request path.

Alert on sustained increases in `error`, `unconfirmed` and `uncertain`, abnormal `in_progress` rates and latency percentiles, and a spike in fingerprint mismatches after SDK or deployment changes. Inspect storage health separately. For unresolved record age/backlog, monitor aggregate state using a read-only operational job, without selecting response bytes or unique IDs. The available `created_at` is creation time, not the time a row became uncertain. Do not turn these dashboards into automatic reset or retry triggers.

## Read-only incident health reports

The repository contains provider-specific SQL under [ops/sql](../ops/sql). Run only using an application-approved monitoring identity with read-only access (SQLite: read-only connection to a supported local file, with the required WAL access). They return **only** bucket, record count and oldest creation age in seconds; missing buckets mean zero. No key, owner token, caller identity, raw header or replay body is selected. Use database time; creation age is not age since a transition. SQL Server uses UTC and second-boundary granularity. Future timestamps are clamped to zero age.

| Bucket | Interpretation and operator action |
| --- | --- |
| processing_active | An unexpired committed Standard claim exists; this does not prove a worker is alive. Monitor, do not reset. |
| processing_expired | Lease expired or missing; investigate as potentially uncertain. Do not assume that the business operation stopped. |
| uncertain | Outcome not proven; preserve the record and use the manual reconciliation procedure above. |
| completed_retained | Within configured retention; preserve result. |
| completed_expired | Eligible for the existing bounded completed-only cleanup, subject to retention policy. |
| invalid_state | Unexpected state/missing completion expiry; investigate storage integrity, do not repair automatically. |

These are aggregate full-table reads, not a request-path endpoint or automatic cleanup. Run with appropriate query timeout, cadence and least privilege; a large table may be expensive or contend with writers. The SQL is intentionally not a migration. The diagnostic query never changes data or grants any permissions. A stalled claim or absent record is **not proof** of rollback. Business-specific reconciliation, authorization to reopen a command, and any new generic mutation API are separate product/security decisions. Use domain identifiers and authorized application audit records; avoid sensitive data in monitoring and incident exports.
