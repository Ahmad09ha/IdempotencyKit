# Security and operations readiness

> This is a release-readiness checklist, not a production security certification. See [Operations](OPERATIONS.md), [verification evidence](VERIFICATION.md), and [security/performance hardening lab](SECURITY_PERFORMANCE_LAB.md) for observed results and limitations.

## Security requirements

- Authenticate and authorize **before replaying** stored responses.
- Scope identities by service, operation, tenant and authenticated principal (or a clearly defined client scope).
- Never accept a global user-supplied key as the sole basis for exposing an old response.
- Do not persist credentials, authentication headers, raw API keys, bearer tokens, or unrestricted request bodies in diagnostic logs.
- Prefer hashed internal lookup IDs and bounded metadata; define encryption/retention needs for stored response bodies.
- Protect keys from injection, unbounded size, high-cardinality abuse and hostile payloads; enforce key/body/response limits.
- Allowlist replayable response headers. Never blindly replay `Set-Cookie`, `Authorization`, hop-by-hop headers, or stale `Content-Length`.
- Detect and reject a different fingerprint on the same scoped key.
- Require an explicit application authorization decision for resources that may be revoked between the original request and retry.
- Treat idempotency as a **correctness** control, not a complete fraud, replay-attack or rate-limiting defense.

## Storage and data lifecycle

- Durable uniqueness and atomicity depend on real PostgreSQL transactions/constraints.
- Define retention guarantees for **completed**, **processing**, and **uncertain** records separately.
- Expired records can be purged only after the replay window and outstanding recovery requirements are met.
- Replaying a key after expiry may be a fresh command; domain-level uniqueness must still cover operations such as financial transfer/checkouts.
- Any Redis Standard-mode store must document persistence, expiry, failover and eviction risks. Cache best-effort behavior must not be sold as transactional durability.
- Document backup/restore and migration behavior, including cross-version fingerprints and in-flight records.

## Operational metrics

Proposed counters / histograms / gauges:

- `idempotency_claim_total` (new / existing / error)
- `idempotency_replay_total`
- `idempotency_mismatch_total`
- `idempotency_inflight_conflict_total`
- `idempotency_uncertain_total` and age of oldest unresolved record
- `idempotency_storage_error_total`
- `idempotency_handler_duration_seconds`
- `idempotency_claim_wait_seconds`
- `idempotency_record_age_seconds` / retention cleanup counts

Metric labels must remain low-cardinality: avoid raw keys, user IDs, request bodies and order IDs in label dimensions.

## Required operations playbooks

**Storage outage:** fail closed for protected operations; alert, restore store, verify state before resuming.

**Uncertain Standard-mode operation:** examine known business key, inspect authoritative database result, reconcile whether to preserve completed state or allow a new attempt. Do not automatically replay arbitrary side effects.

**Unexpected key conflict spike:** investigate client key reuse, fingerprint contract/version drift and request canonicalization differences.

**Accumulating records:** confirm retention and cleanup policy before deleting; do not drop still-replayable or unresolved states.

**Version upgrade:** check backward-compatible fingerprint and serialized response formats. Record version metadata to avoid silently misclassifying in-flight requests.

## Readiness before a production release

- [ ] Real PostgreSQL tests with concurrency, crash/restart and multiple replicas.
- [ ] Documented isolation and data access tests.
- [ ] Response replay security tests.
- [ ] Bounded memory/size and rate-limit behavior.
- [ ] Storage failure and stale-owner fault injection.
- [ ] Documented recovery and retention runbook.
- [ ] Package dependency and license review.
- [ ] Verifiable, dated results for supported operating environments.
