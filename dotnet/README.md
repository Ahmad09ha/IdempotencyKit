# .NET 10 workspace

The solution contains core fingerprint types, a **working PostgreSQL Standard-mode storage adapter**, an ASP.NET Core integration scaffold, xUnit tests, and a sample health-only API.

- `IdempotencyKit.Core`: scoped identity, canonical JSON request fingerprints, storage contracts.
- `IdempotencyKit.Postgres`: atomic claims, owner-lease fencing, completed JSON response persistence and replay data, conservative `Uncertain` handling and explicit purge.
- `IdempotencyKit.AspNetCore`: **not implemented** beyond scaffold.
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

This is not a complete sample API. **Do not assume calling TryClaim makes arbitrary subsequent business writes atomic**. The eventual ASP.NET Core middleware and Transactional mode are future milestones.

The `Orders.AspNetCore` sample still exposes `GET /health` only.
