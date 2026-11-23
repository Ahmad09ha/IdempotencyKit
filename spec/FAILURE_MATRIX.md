# Failure and Recovery Matrix (proposed acceptance criteria)

> This remains the **full acceptance backlog**, not a list of universally passing tests. Initial transactional integration tests now exercise commit + replay, callback exception rollback, response-validation rollback, 24 concurrent contenders and deliberately terminated PostgreSQL backends **before COMMIT**. Real Node **worker-process SIGKILL** and **live NestJS Express HTTP-server SIGKILL before/after COMMIT** have now passed, including same-key HTTP retry to a surviving server. Equivalent ASP.NET Core process-crash, database failover and network partition labs remain pending. See [Verification Evidence](../docs/VERIFICATION.md). See [Transactional Mode v1](TRANSACTIONAL_MODE_V1.md).

| Scenario | Standard-mode expectation | Transactional-mode expectation |
| --- | --- | --- |
| New key and normal success | Claim, execute, store result | Atomically commit domain changes + stored result |
| Retry completed same key/same body | Replay stored response | Replay stored response |
| Same key/different body | `422`, handler not run | `422`, handler not run |
| Missing required key | `400`, handler not run | `400`, handler not run |
| 100 parallel matching requests | One lease owner at a time; others conflict/retry; no unproven claim about business effects after lease expiry | One committed mutation and one completed result |
| Two API replicas receive same key | Coordinate through shared atomic store | Coordinate through PostgreSQL unique constraint/transactions |
| Handler fails before any business commit | Safe error policy; release/reconcile under documented rules | Transaction rolls back |
| Process dies before business commit | Recover stale processing claim cautiously | Both domain + completion roll back |
| Business DB commits, process dies before recording result | **Unknown effect: do not blindly rerun** | Not possible as a separate commit when same transaction is correctly used |
| Process dies after transaction commit, before HTTP response | Replay only if result was durably stored | Replay committed result |
| Process dies after result recording, before HTTP reply | Replay stored result | Replay committed result |
| Lease expires during slow in-flight operation | Old worker cannot complete under stale token; business effect still needs independent protection | Lock/transaction bounded; no free-floating lease guarantee |
| Claim owner resumes after another owner acquired | Ownership fencing prevents stale record completion; effects need additional protections | Database serialization/uniqueness coordinates transaction |
| Storage unreachable before claiming | `503`; handler not run | Reject, no business mutation |
| Storage fails at result-completion time | Report/record uncertain outcome; never assume handler did not commit | Transaction rolls back unless commit already succeeded; commit-ack ambiguity resolved by retry lookup |
| Database commit succeeds but client receives network error | Outcome may be uncertain without atomic result | Retry resolves durable recorded outcome |
| Unrelated users send same raw key | Independent scopes; never leak cached data | Independent scopes; never leak cached data |
| Auth rights revoked before replay | Deny according to current policy | Deny according to current policy |
| Key expires after retention | New operation possible: business constraints still apply | New operation possible: business constraints still apply |
| Redis record evicted early (future adapter) | Retention guarantee is lost; document/fail/policy as appropriate | Redis cannot substitute for shared durable DB transaction |
| Payload JSON field order changes | Deterministic fingerprint according to chosen canonicalization policy | Identical behavior and fixtures |
| Payload contains precision/Unicode edge cases | Same explicit cross-language fingerprint behavior | Same explicit cross-language fingerprint behavior |
| Response contains `Set-Cookie` or secret headers | Never blindly persist/replay them | Never blindly persist/replay them |
| Payment API called in handler | Provider/business idempotency still required | Provider/business idempotency still required |

## Fault-injection checkpoints for Orders sample

1. Before scoped key claim.
2. Immediately after claim and before handler invocation.
3. Before inserting the order.
4. After inserting order but before business commit.
5. After business commit, before Standard-mode completion.
6. After broker/other external side effect (illustrate why not covered).
7. After transactional commit, before writing HTTP response.
8. After response bytes are sent but before connection closure.

## How we prove each case

- **Unit tests:** key validation, fingerprints, state transitions, header allowlists, serialized response compatibility.
- **Integration tests:** PostgreSQL unique-index races, transaction rollback, lock wait behavior, multi-process same-key requests.
- **Failure lab:** Docker-based API replicas, controllable kill points, crash/restart and reconciliation; assert database row counts and returned responses.
- **Contract fixtures:** identical language-neutral scenarios executed by .NET and NestJS.
- **Observability:** traces, idempotency outcome metrics, age/volume of uncertain records and storage failure alerts.

All results should be date-stamped and reported in an eventual `docs/VERIFICATION.md` (file to be added when tests exist), distinguishing verified and unverified platforms.
