# .NET 10 workspace

The solution includes scoped fingerprints, Standard-mode HTTP, a PostgreSQL transaction executor, **EF Core 10 shared-transaction integration**, **transactional ASP.NET Core PostgreSQL endpoints**, xUnit integration tests and a runnable SQLite Orders sample.

- `IdempotencyKit.Core`: scoped identity, canonical JSON fingerprints, `IIdempotencyStore`, and provider-neutral `ITransactionalIdempotencyExecutor<TConnection,TTransaction>` + `TransactionalExecutionResult`.
- `IdempotencyKit.Postgres`: PostgreSQL Standard-mode store plus atomic transaction executor, implementing the shared core interface.
- `IdempotencyKit.SqlServer`: SQL Server 2022 Standard-mode store + `SqlServerTransactionalExecutor` using a transaction-owned per-key application lock and a shared `SqlConnection`/`SqlTransaction` callback.
- `IdempotencyKit.Sqlite`: file-backed Standard store + `SqliteTransactionalExecutor`, WAL/FULL initialization and shared `SqliteConnection` / `SqliteTransaction`; SQLite-wide busy fails closed.
- `IdempotencyKit.EntityFrameworkCore.Sqlite`: `SqliteEfCoreTransactionalExecutor<TContext>` verifies exact same SQLite connection and joins the existing transaction.
- `IdempotencyKit.AspNetCore.Sqlite`: `AddSqliteTransactionalIdempotency` + `MapSqliteTransactionalPost` for authenticated transaction-aware JSON routes; file-wide busy is 503.
- `IdempotencyKit.EntityFrameworkCore.SqlServer`: `SqlServerEfCoreTransactionalExecutor<TContext>` validates exact `SqlConnection`, enlists `UseTransactionAsync`, and auto-saves pending changes before COMMIT.
- `IdempotencyKit.AspNetCore.SqlServer`: `AddSqlServerTransactionalIdempotency` and `MapSqlServerTransactionalPost` for authenticated SQL Server transaction-aware JSON endpoints.
- `IdempotencyKit.AspNetCore`: database-neutral HTTP input preparation and Standard-mode endpoint middleware.
- `IdempotencyKit.AspNetCore.Postgres`: `MapTransactionalPost` and `AddPostgresTransactionalIdempotency` for authenticated PostgreSQL transactions.
- `IdempotencyKit.EntityFrameworkCore.Postgres`: `PostgresEfCoreTransactionalExecutor<TContext>` verifies same connection and joins the Npgsql transaction.
- `IdempotencyKit.Postgres.Tests`: real PostgreSQL integration tests.

**Provider expansion:** SQL Server core, EF Core 10 and transactional ASP.NET Core HTTP are implemented and tested; SQLite core, EF Core 10 and authenticated ASP.NET Core HTTP are implemented and tested against real local files. See [SQLite EF Core + HTTP](../docs/SQLITE_EFCORE_HTTP_V1.md). See [Provider Architecture v1](../spec/PROVIDER_ARCHITECTURE_V1.md) for the generic interface and the database-specific concurrency contract.

## Build

~~~bash
cd dotnet
dotnet restore IdempotencyKit.slnx
dotnet build IdempotencyKit.slnx -c Release
~~~

## Integration tests

Apply the SQL migration `../spec/sql/postgres/001_idempotencykit_records.sql` first. Set `IDEMPOTENCYKIT_TEST_POSTGRES` to an isolated PostgreSQL test database (e.g. `Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=testpass`), then:

~~~bash
dotnet test IdempotencyKit.slnx -c Release
~~~

The tests use the **real** DB and require a working connection. See `../docs/GETTING_STARTED.md`.

## Programmatic storage usage (illustration of implemented store API)

~~~csharp
await using var dataSource = Npgsql.NpgsqlDataSource.Create(connectionString);
IIdempotencyStore store = new PostgresIdempotencyStore(dataSource);

var claim = await store.TryClaimAsync(scopeKeyHash, fingerprint, TimeSpan.FromSeconds(30));
// ClaimStatus.Acquired => hold owner token; Completed => stored response;
// InProgress / FingerprintMismatch / Uncertain => no handler execution.
~~~

This is not a complete sample API. **Do not assume calling TryClaim makes arbitrary subsequent business writes atomic**. Standard-mode HTTP middleware is implemented, but it does not make business writes atomic with the result. Transactional Mode is now accessible through the Npgsql callback, EF Core 10 adapter and explicit PostgreSQL transactional endpoints. Standard-mode annotations remain non-transactional.

See [Transactional HTTP + EF Core](../docs/TRANSACTIONAL_HTTP_AND_EFCORE_V1.md) and [Transactional Mode v1](../spec/TRANSACTIONAL_MODE_V1.md) and [`PostgresTransactionalTests.cs`](tests/IdempotencyKit.Postgres.Tests/PostgresTransactionalTests.cs) for the fully tested atomic callback API. Do not reuse Standard-mode `[Idempotent]` on a route executing this transactional callback.

The `Orders.AspNetCore` sample runs an authenticated local SQLite transaction and exposes `POST /orders`. For tested protected endpoint examples, see [`HttpStandardTests.cs`](tests/IdempotencyKit.AspNetCore.Tests/HttpStandardTests.cs) and [HTTP integration guide](../docs/HTTP_STANDARD_V1.md).
