# Relational mixed-runtime HTTP lab

This test-only lab starts real Kestrel and NestJS Express HTTP servers with the same namespace, operation, principal, tenant, route and durable database. It exercises the public HTTP adapters without modifying their APIs or production code.

Acceptance criteria for each of PostgreSQL 17 and SQL Server 2022:

- Both directions of cross-runtime replay preserve original bytes and Location, including reordered JSON, mismatch 422 and unauthenticated retry 401.
- Held NestJS transaction causes actual same-key in-progress 409 from .NET, then byte-identical replay after commit.
- Held Kestrel transaction causes actual same-key in-progress 409 from Node; hard kill before commit permits same-key retry through Node and replay through a fresh/independent .NET host.
- Hard kill Kestrel after commit but before headers; Node replays the original saved response.
- 24 simultaneous mixed-framework requests converge on one committed domain row and completed record; bounded retries use the SAME key.
- Independent SQL reads verify scope key, canonical fingerprint, record state, domain row and exact response bytes.

Run only with the disposable loopback services provisioned by [Relational Mixed Runtime HTTP CI](../.github/workflows/relational-interop-ci.yml). This is acceptance intent until the CI results are recorded. Not a general guarantee for arbitrary divergent application contracts, external effects, database failover, physical disk exhaustion or multi-region deployments.

The crash fixture and its earlier verified provider-specific process tests are described in [Relational HTTP crash lab](RELATIONAL_HTTP_CRASH_LAB.md).

## Observed results

[PR #13](https://github.com/Ahmad09ha/IdempotencyKit/pull/13), head `156a1f0a1fb1d96a89fa7ce540c6849674f82f07`: all **10 tests passed (5 PostgreSQL + 5 SQL Server)** in [mixed-runtime CI run 45481033033](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45481033033). All four other workflows passed on the same head. Merged to `main` as `572de62e7bb10848f66a4756e7c14204aecad129`.
