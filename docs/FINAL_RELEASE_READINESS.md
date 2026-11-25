# Final release readiness — preview candidate

Status: PR #17 passed all nine exact-head workflows and was merged as `88f593f`; no packages published.

## Focused security review and evidence register

| Finding | Disposition / test |
| --- | --- |
| JSON body with 204/205 could be stored although HTTP forbids content; 206 needs range semantics not represented by our safe headers. | Reject all three before commit and when reading tampered records. Existing valid JSON status/body pairs are unchanged. |
| Non-ASCII replay header values can pass provider validation but default Kestrel only supports ASCII; a post-COMMIT write failure would violate wire replay. | Restrict portable persisted header values and Content-Type to printable ASCII plus HTAB in both runtimes. International content remains supported in JSON. Test rejected Unicode and Latin-1 headers as well as tampered rows. |
| Stored response can contain business-sensitive information. | Existing scoped authentication, current operation authorization, storage protection and bounded retention remain mandatory. At-rest encryption and trusted valid-row forgery by a privileged DB actor are outside the package guarantee. |
| Async SQLite APIs are synchronous underneath. | Do not promise async disk scalability; use WAL and short callbacks; no unsafe pool/durability changes. |
| Known dependency advisories | Existing NuGet/npm audit gates; no claim of exhaustive vulnerability detection. |
| Uncertain Standard claims | Preserve, report and reconcile from authoritative business evidence. No blind retry/reset. |
| Independent security assessment | **Open**: this is a focused maintainer-style code review, not an independent audit or penetration test. |

Primary sources and engineering implications: [RFC 9110 sections 15.3.5–7](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.3.5) (204/205 cannot contain content; 206 range semantics); [ASP.NET Core 10 response-header encoding](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserveroptions.responseheaderencodingselector?view=aspnetcore-10.0) (default ASCII); [Node 24 HTTP header validation](https://nodejs.org/docs/latest-v24.x/api/http.html#httpvalidateheadervaluename-value) (invalid field values throw); [SQLite transaction errors](https://www.sqlite.org/lang_transaction.html#response_to_errors_within_a_transaction) (explicit rollback after error); [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async). These constraints protect the existing portable wire contract rather than add new supported response shapes.

## Candidate gates

- Real database regression, process crash, mixed-runtime and stress/outage tests; package install and dependency checks.
- Isolated filesystem ENOSPC verification; extended soak with actual duration, error counts and sampled memory. No claim of physical-device failure or leak freedom.
- Developer quick-start and existing API/operations guides aligned to source, with sample authentication explicitly non-production.
- Before publication: independent review disposition, registry ownership, release identity, immutable version and publication approval. The maintainer selected MIT on 2026-11-25. The existing public NuGet ID `IdempotencyKit.AspNetCore` requires resolution. No automatic registry publishing or new version in this change.

## Filesystem and time-based soak labs

The new readiness workflow mounts a 32 MiB ext4 filesystem on a regular temporary image file in the hosted runner. It never formats a device or fills the host filesystem. Both SQLite sample processes use local files on that image. Real writes hit ENOSPC (also proven by a second independent write); a new key must return 503, a readable completed result may replay exactly or return 503, and freeing the fixture file must restore same-key recovery with one durable write. This verifies an actual isolated filesystem capacity fault, not physical device failure, power loss, disk firmware or production quota behavior.

The six runtime/provider combinations run 16 closed-loop clients for **at least 60 seconds each** by default, with optional 180/300-second manually triggered jobs. Fixed replay keys and up to 512 fresh keys bound retained test data. Sample every ten seconds; record raw HTTP status counts, approximate histogram upper-bound latency percentiles, elapsed time, sampled RSS, and relational Node pool occupancy/drain. Read back every new key and original replay after the timed window; verify distinct order count. No timed transport errors allowed. Node relational RSS includes the test client. These are time-based CI soak checks, not a multi-hour leak test, performance guarantee or independent review. The PostgreSQL/SQL Server live HTTP fixtures also try 204/205/206 and non-ASCII headers after a business INSERT, verify 503 and no committed state, then perform a safe retry.

## Observed final readiness evidence

[PR #17](https://github.com/Ahmad09ha/IdempotencyKit/pull/17), verified source `d385771f22e4cac9fa56e700cd0f1b72716bf2ab`: [final readiness CI](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45611565795) passed all three jobs and the other eight regression/audit/package workflows passed. Both relational jobs rejected invalid JSON wire statuses/nonportable headers before commit. Both SQLite processes hit actual independent ext4 ENOSPC (fresh key 503, existing key 201), then restored one-write same-key recovery after freeing the fixture.

| Provider | .NET attempts in 60s | Node attempts in 60s |
| --- | ---: | ---: |
| PostgreSQL | 146891 | 91534 |
| SQL Server | 73815 | 34143 |
| SQLite | 130768 | 113941 |
| Total | 351474 | 239618 |

591,092 measured attempts across six windows, 586,471 HTTP 201 and 4,621 intentional 422; 3,019 verified distinct orders including warm-ups. No unexpected statuses or transport errors in these windows. Both relational Node pools drained after load. Five stress-only tests in each relational readiness job were intentionally skipped and separately covered by the successful stress workflow. Sampled RSS is not a multi-hour memory-leak proof. An independent review and publication approval remain open.
