# IdempotencyKit for .NET

MIT-licensed project source; see the `LICENSE` in this package. Third-party dependencies retain their own license terms.

Preview packages target .NET 10. IdempotencyKit helps write APIs recognize a retry and replay a recorded HTTP response without repeating a committed business write **when that write and its response share one supported database transaction**.

- Core and ASP.NET Core Standard mode: scoped request keys, fingerprints, leases and HTTP response replay. A crash can leave the business outcome uncertain.
- PostgreSQL, SQL Server 2022 and single-host local SQLite: provider-specific store, explicit transaction executor, ASP.NET Core endpoint and EF Core bridge.
- No exactly-once guarantee for external HTTP calls, emails, payments, or writes outside the supplied transaction.

See the [getting started guide](https://github.com/Ahmad09ha/IdempotencyKit/blob/main/docs/GETTING_STARTED.md), [guarantees](https://github.com/Ahmad09ha/IdempotencyKit/blob/main/spec/GUARANTEES.md), and [test evidence](https://github.com/Ahmad09ha/IdempotencyKit/blob/main/docs/VERIFICATION.md). Database core packages include the versioned SQL migration under `sql/`; apply it as part of deployment, not per request.
