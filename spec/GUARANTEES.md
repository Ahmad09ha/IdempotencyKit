# Guarantees, Preconditions, and Non-Guarantees

> **Mixed verification status.** Scoped fingerprints, PostgreSQL Standard mode, transactional callbacks, EF Core 10 shared transactions, and explicit ASP.NET Core/NestJS Express transactional HTTP integration now have real PostgreSQL tests. Actual NestJS HTTP-server SIGKILL before/after COMMIT is verified. This remains **not production certification**: external effects, SQL Server/SQLite, equivalent ASP.NET process-crash testing, database failover and reconciliation remain outside these claims. See [Verification Evidence](../docs/VERIFICATION.md).

## G1 — Scoped isolation

A key must be scoped by namespace, operation and appropriate authenticated tenant/principal boundary. A response must never be replayed to another principal/tenant merely because they supplied the same string. Authorization checks remain necessary for replays.

## G2 — Content consistency

A scope/key reused with a different fingerprint returns a conflict-class client error (proposed `422`). Never replay a response for a mismatched request.

## G3 — Safe concurrent claiming

Multiple API replicas must coordinate via durable, atomic storage primitives. A read-then-insert without a uniqueness constraint is insufficient. At most one active owner may hold an unexpired **Standard** lease for a given scope/key; possession of an expired token does **not** authorize later completion.

## G4 — Standard-mode replay

After a result is recorded as completed and remains available for the documented retention interval, a matching retry replays that supported response without re-running the handler. A request may execute again **after retention expiry** unless an independent business constraint prevents it.

### Standard-mode critical limitation

~~~text
claim -> business DB commit -> process crash -> completion not recorded
~~~

There is **no atomicity** across a separate idempotency store and arbitrary handler transaction. An expired lease is not evidence the business action failed. The safe default is an uncertain/reconciliation state; automatic retry is **not** a generic safety guarantee.

Redis ownership tokens and leases protect the idempotency record, **not arbitrary side effects executed by an old worker**. Storage eviction, crash persistence, failover and replication choices also constrain any assurance.

## G5 — Transactional-mode atomicity

For supported operations where the **business mutation and the completed response record share one ACID PostgreSQL transaction**:

- If the transaction rolls back, both effects are absent.
- If it commits, both effects are durable according to the database's durability configuration.
- A retry with the same scope/key/fingerprint after commit retrieves the result without re-running the transaction's handler.
- Concurrent attempts rely on database uniqueness/locking and bounded conflict behavior.

This applies to database writes that actually participate in that same transaction. It does **not** make external payment gateways, emails, cross-database writes, calls to other APIs, caches, broker publications, or arbitrary side effects exactly-once.

Transaction-aware execution is now exposed through the **explicit Npgsql and node-postgres callbacks**; the existing HTTP decorator/attribute remains Standard-mode only. Simply tagging a controller with that decorator/attribute is **not** a transactional guarantee. See [Transactional Mode v1](TRANSACTIONAL_MODE_V1.md).

## G6 — Fail-closed behavior

If a protected endpoint cannot query/claim required storage, the engine rejects the request by default rather than executing blindly. Recovery must be explicit and observable.

## G7 — Idempotency is not business uniqueness

Different keys might describe the same business order or payment. The domain must still enforce unique business constraints as needed (e.g. unique checkout intent/order reference). Duplicate prevention on one header cannot correct improperly duplicated business operations across multiple distinct keys.

## Guarantees that are explicitly NOT made

- “Exactly once HTTP delivery” or “exactly once external side effects.”
- Deterministic global request ordering.
- Safe automatic execution on any expired claim.
- Perpetual deduplication after retention/eviction.
- Production safety without the correct database, transaction, deployment, access-control and retention configuration.
- Safe handling of unsupported streaming, manual responses, or arbitrary bodies.

## Evidence required before marketing a guarantee

For each guarantee above, attach deterministic integration and fault tests against **real PostgreSQL**, across at least two API replicas, including intentionally killed processes at relevant boundaries. Record environment/version, test command, outcomes, known limitations and pending tests. Do not equate source code or passing mocks with proven recovery.

Related: [HTTP Contract](HTTP_CONTRACT.md), [Failure Matrix](FAILURE_MATRIX.md), [Architecture](../docs/ARCHITECTURE.md).
