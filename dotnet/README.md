# .NET 10 workspace

The solution contains core fingerprint types, a **working PostgreSQL Standard-mode storage adapter**, an ASP.NET Core Standard-mode integration via middleware and endpoint metadata, xUnit tests, and a sample health-only API.

- `IdempotencyKit.Core`: scoped identity, canonical JSON request fingerprints, storage contracts.
- `IdempotencyKit.Postgres`: atomic claims, owner-lease fencing, completed JSON response persistence and replay data, conservative `Uncertain` handling and explicit purge.
- `IdempotencyKit.AspNetCore`: implemented `AddIdempotencyKit`, `UseIdempotencyKit`, `.WithIdempotency(...)`, and MVC `[Idempotent(...)]`.
- `IdempotencyKit.Postgres.Tests`: real PostgreSQL integration tests.

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

This is not a complete sample API. **Do not assume calling TryClaim makes arbitrary subsequent business writes atomic**. Standard-mode HTTP middleware is implemented, but it does not make business writes atomic with the result. Transactional mode remains a future milestone.

The `Orders.AspNetCore` sample still exposes `GET /health` only. For tested protected endpoint examples, see [`HttpStandardTests.cs`](tests/IdempotencyKit.AspNetCore.Tests/HttpStandardTests.cs) and [HTTP integration guide](../docs/HTTP_STANDARD_V1.md).
