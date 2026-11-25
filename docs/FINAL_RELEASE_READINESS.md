# Final release readiness — preview candidate

Status at implementation: verification pending; no packages published.

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
- Before publication: independent review disposition, maintainer-approved license, registry ownership, release identity, immutable version and publication approval. No automatic registry publishing or new version in this change.
