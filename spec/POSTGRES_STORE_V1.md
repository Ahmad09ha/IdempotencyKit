# PostgreSQL Standard-mode Store v1

**Status: initial implementation, pending release hardening.** The PostgreSQL adapters and real-database integration tests exist for .NET and TypeScript. This specification describes *storage*, not an HTTP middleware or a transaction coordinator for application business operations.

## Why a database-backed store?

An incoming retry may represent the **same logical request** as a previous HTTP attempt. To coordinate multiple API instances, the server needs a shared authoritative record, not an in-memory dictionary.

Each record is keyed by the **SHA-256 scoped identity hash**, with a separate **request fingerprint**. The first client request attempts to claim the record using one PostgreSQL statement:

~~~sql
INSERT INTO idempotencykit_records (...)
VALUES (...)
ON CONFLICT (scope_key_hash) DO NOTHING
RETURNING owner_token;
~~~

The PostgreSQL primary key serializes competing inserts. This is **not** an unsafe read-then-insert flow. The owning token is a randomly generated UUID, valid only during an unexpired processing lease.

## States and observed claim outcomes

| Stored state | Claim API result | What the caller should do |
| --- | --- | --- |
| No record yet | `Acquired` (owner token) | Eligible to execute handler under chosen safety policy |
| `processing`, unexpired, same fingerprint | `InProgress` | Do not execute handler; future HTTP adapter may return `409` |
| `processing`, expired, same fingerprint | `Uncertain` (atomic transition) | Do **not** automatically re-execute; reconcile prior business result |
| `uncertain`, same fingerprint | `Uncertain` | Do not execute; operational reconciliation needed |
| `completed`, same fingerprint | `Completed` (stored response) | Caller may replay *after* checking authorization |
| Any existing state, different fingerprint | `FingerprintMismatch` | Never execute or replay; future HTTP adapter may return `422` |

A completed result includes original HTTP status, response bytes, JSON content type, and allowed headers. **The storage layer does not itself write the HTTP response.**

### Critical failure window

~~~text
Claim acquired -> business DB commit -> worker crash -> no completed storage result
~~~

This is **Standard-mode uncertainty**, not a safe retry. If the lease expires before the worker marks completion, a later attempt transitions the record to `uncertain`. It does not reclaim the key. The original worker might already have committed an order, sent money, or made other external effects.

**Leases and fencing tokens prevent stale store-record updates, not arbitrary external side effects.** If you need atomic database business writes and idempotency completion, a *separate Transactional mode* must join the business write transaction (not implemented here).

## Atomic operations

- `TryClaim`: unique insert or read the matching record and resolve a result. If the processing lease expired, it attempts an atomic update to `uncertain`.
- `TryComplete`: sets `completed` and persists a bounded response only if the fingerprint matches, the owner token is current and the lease is **still valid**. Returns `false` if a race/lost lease blocks completion.
- `TryExtendLease`: renews only the current, unexpired lease; a late heartbeat cannot resurrect an expired lease.
- `PurgeExpiredCompleted`: deletes expired **completed** records in limited batches using `FOR UPDATE SKIP LOCKED`. Never deletes processing/uncertain records automatically.

Every operation uses parameterized SQL; neither adapter creates tables in request paths.

## Data limits and access policy

- Input scope key and fingerprint: `v1:` plus lowercase 64-character hex digest.
- Lease: 1 ms to 24 hours; response retention: 1 ms to 365 days.
- Only HTTP **2xx** result statuses are stored in this first iteration.
- Response body is raw bytes with maximum **256 KiB**; content type must be `application/json` or `application/problem+json`, optionally with parameters, and must not contain CR/LF or exceed 128 characters.
- Allowed stored response headers: `Location`, `ETag` and `Content-Language` (case-insensitive, canonicalized lowercase). `Set-Cookie` and arbitrary headers are rejected; CR/LF injection is rejected.
- Completed records are retained **at least** until their expiration unless the database is manually modified. If no cleanup runs, an expired record may remain replayable *longer*. Once removed, the same key can be treated as new.
- Unknown processing outcomes are never auto-purged. Operations teams must reconcile them against authoritative business identifiers.
- The hash is not authorization. Only trusted server-side principal/tenant scope builders and authenticated/authorized callers may read or replay records.

## Migration

Source of truth: [`spec/sql/postgres/001_idempotencykit_records.sql`](sql/postgres/001_idempotencykit_records.sql).

The schema uses `idempotencykit_records` with a primary key, constrained lifecycle state, fenced UUID token, lease timestamps, completed response body/headers, and completion/expiry timestamps. A partial expiry index supports bounded cleanup.

Apply the migration once using a managed deployment migration mechanism. Do not run schema creation at request time. The demo/integration tests apply this migration automatically to their own isolated PostgreSQL databases.

## Compatibility

Both runtimes implement the **same conceptual decisions** and SQL schema. Tests in each language exercise the same lifecycle (atomic claims, conflicts, mismatch, replay, expiry, owner fencing, invalid headers, cleanup).

No claim is made yet about multi-process crash injection, HTTP response serialization, provider-failover resilience, or a transactional business write. Release readiness depends on deeper integration tests and operational review.

## Next steps

1. Verify both CI jobs against real PostgreSQL, document evidence.
2. Introduce a carefully scoped application-level reconciliation policy for uncertain records.
3. Implement ASP.NET Core and NestJS endpoint adapters with authentication/authorization ordering, protected response handling, and safe replay.
4. Add an explicit Transactional mode for same-database business mutations.
