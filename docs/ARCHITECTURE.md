# Architecture and design decisions (draft)

> **Mixed implementation state:** the .NET and NestJS/Express Standard-mode HTTP adapters, fingerprint cores, PostgreSQL stores and integration tests now exist. The **Transactional mode**, generalized engine abstractions, Fastify support, crash labs and production release processes remain proposed. See [HTTP Standard Mode v1](HTTP_STANDARD_V1.md).

## Goals

- Provide native, idiomatic ASP.NET Core and NestJS integrations with a **common external behavior**.
- Make retry behavior predictable, multi-instance safe, tenant/principal scoped and observable.
- Separate easy-to-use Standard mode from strongly bounded, transaction-aware mode.
- Prove claims through real PostgreSQL concurrency/crash tests.

## Non-goals

No magic exactly-once operations across independent databases, payment providers, queues, emails, file storage, or streaming HTTP bodies. No automatic retry of business effects when a previous execution outcome is unprovable.

## Logical components

~~~text
                   HTTP / JSON write request
                             |
             Authentication, authorization, validation
                             |
       +---------------------+---------------------+
       |                                           |
 ASP.NET Core filter                         NestJS interceptor
       |                                           |
 .NET IdempotencyEngine                   TS IdempotencyEngine
       |                                           |
 ScopeResolver + Fingerprinter + ResultSerializer
                             |
                 Atomic IdempotencyStore
                             |
              PostgreSQL (initial adapter)
                             |
             +---------------+---------------+
             |                               |
       Standard mode                 Transactional mode
  leased claim + completed       shared DB transaction:
  response + uncertain state     business write + result
             |                               |
             +-------------------------------+
                             |
                    HTTP response/replay
~~~

The same architecture is implemented **twice**, not shared as runtime code. The reusable artifact across ecosystems is the **contract**, including sample inputs, hashes, response/error fixtures, and failure expectations.

## Storage contract (conceptual)

- `TryClaim(scopeKey, fingerprint, ownerToken, leaseExpiry)`: atomic claim, conflict or existing record.
- `Read(scopeKey)`: return existing fingerprint/state/result with consistent semantics.
- `Complete(scopeKey, ownerToken, response)`: conditional completion only by current owner.
- `ExtendLease(scopeKey, ownerToken, newExpiry)`: conditional heartbeat (optional initially).
- `MarkUncertain(scopeKey, ownerToken, reason)`: retain evidence for safe recovery.
- `GetRetentionPolicy` and cleanup: never delete earlier than committed retention limits.

Transactional integration is **not** just a generic call to `Complete` on an unrelated connection. It must write idempotency state **inside the caller's actual business database transaction**, using one underlying connection and transaction. A storage adapter that cannot join that transaction cannot advertise transactional guarantees.

## Proposed PostgreSQL record model

| Column | Role |
| --- | --- |
| `scope_key_hash` | Unique scoped key identity (primary key/index) |
| `fingerprint_version` / `request_hash` | Detect changed payloads and evolving hashing algorithms |
| `state` | Processing / Completed / Uncertain for Standard-mode records |
| `owner_token` | Fences stale Standard-mode claim owners |
| `lease_expires_at_utc` | Bounded Standard-mode coordination |
| `http_status` / `content_type` / `response_body` | Supported replayable result |
| `response_headers` | Small safe allowlisted map |
| `created_at_utc` / `completed_at_utc` / `expires_at_utc` | Auditing and retention |
| `last_error_code` | Bounded diagnostics; no sensitive exception dumps |

Exact SQL schema, indices and migration ownership remain open design decisions. Unique constraints and database transaction isolation must be proved against real PostgreSQL, not only mocks/in-memory stores.

## Standard mode state machine

~~~text
Absent --atomic claim--> Processing --success + owner token--> Completed
                             |
                             +--crash/lease expiry--> Uncertain / Reconcile

Completed + same fingerprint --> Replay (without running handler)
Existing + different fingerprint --> 422 (without running handler)
Processing + same fingerprint --> 409 (without running handler)
Uncertain --> manual or domain-supported reconciliation; no blind re-execution
~~~

A lease limits competing ownership, **not** real-world side effects executed by a paused process. Tokens only fence updates to the store; downstream businesses need their own protections.

## Transactional mode lifecycle

1. Resolve scope/key/fingerprint **after** authentication/authorization.
2. Begin the same PostgreSQL transaction used for the business command (or join an existing one).
3. Insert a scoped idempotency marker under a database-enforced uniqueness constraint; bounded concurrent transactions may wait or conflict.
4. Perform the allowed database effects **using the same transaction**.
5. Serialize the supported HTTP result **before committing**.
6. Store completed result and metadata **inside the same transaction**.
7. Commit once. Send HTTP response afterwards.
8. On client retry, read the completed row, verify fingerprint and authorization, then replay.

If a process crashes before step 7, no mutation/result commits. If it crashes after step 7 but before the response, the result can be recovered. A timed-out or ambiguous commit acknowledgement requires read/reconcile rather than an unconditional second execution.

We will explicitly specify how EF Core passes its `DbContext` transaction and how NestJS passes a PostgreSQL transaction context. We should not promise automatic support for arbitrary independent repository connections, multiple ORMs, middleware-managed transactions, or callback code that opens new connections.

## .NET implementation boundaries (planned)

- `IdempotencyKit.Core`: pure contracts, state machine, hashing/serialization interfaces.
- `IdempotencyKit.AspNetCore`: Minimal API endpoint filter and MVC filter/attribute; framework-specific response handling and authorization order.
- `IdempotencyKit.Postgres`: atomic store and compatible EF Core/shared-transaction entry point.
- Explicit integration tests against PostgreSQL and HTTP host; test filters and actual serialization rather than only controller return objects.

## NestJS implementation boundaries (planned)

- `@idempotencykit/core`: pure contracts, state machine, fingerprinting + serializer.
- `@idempotencykit/nestjs`: decorator, dynamic module, interceptor plus appropriate guard ordering for Express/Fastify targets.
- `@idempotencykit/postgres`: store and explicitly supported transaction integration.
- Tests must cover interceptors, RxJS execution and framework response handling; manual raw responses are out of v1 scope.

## Invariants for design review

1. Same scoped key and same fingerprint cannot execute multiple **committed transactional** database effects within retention.
2. Different fingerprint cannot get an old response.
3. Authenticated tenants/principals cannot access another scope's result.
4. Standard lease expiry never silently implies the handler did not commit.
5. Unsupported side effects cannot be advertised as “exactly once.”
6. Tests should state their environment and not upgrade unverified claims to guarantees.

Related: [HTTP Contract](../spec/HTTP_CONTRACT.md), [Guarantees](../spec/GUARANTEES.md), [Failure Matrix](../spec/FAILURE_MATRIX.md).
