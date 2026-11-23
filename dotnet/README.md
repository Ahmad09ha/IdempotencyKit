# .NET workspace (scaffold)

The solution contains a **core contracts library**, separate **ASP.NET Core** and **PostgreSQL** adapter projects, a simple **sample Minimal API**, and a **xUnit** test project.

No request filter, PostgreSQL store, or working idempotency engine has been implemented yet. The sample only exposes `GET /health`.

## Requirements

- .NET 10 SDK (LTS). The solution uses `net10.0` and a `global.json` that allows newer .NET 10 feature bands.
- NuGet package access for the test project.

## Commands

~~~bash
cd dotnet
dotnet restore IdempotencyKit.slnx
dotnet build IdempotencyKit.slnx --configuration Release
dotnet test IdempotencyKit.slnx --configuration Release
dotnet run --project samples/Orders.AspNetCore
~~~

Query `/health` on the URL printed by ASP.NET Core. The response says `"idempotency": "scaffold-only"` to avoid suggesting write requests are protected.

## Next work

1. Freeze request identity and fingerprint test vectors from `../spec`.
2. Build atomic claim/state transitions and a real PostgreSQL integration.
3. Implement ASP.NET Core endpoint filters and MVC integration.
4. Build crash-safe transactional integration and concurrency tests.
