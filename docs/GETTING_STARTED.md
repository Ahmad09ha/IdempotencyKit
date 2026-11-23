# Getting Started: Project Scaffolds

> These are **working-project scaffolds to build and run**, not finished idempotency packages. At this stage there is NO retry protection for write endpoints.

## .NET solution

Requires the .NET 10 SDK:

~~~bash
cd dotnet
dotnet restore IdempotencyKit.slnx
dotnet build IdempotencyKit.slnx -c Release
dotnet test IdempotencyKit.slnx -c Release
dotnet run --project samples/Orders.AspNetCore
~~~

Open the `/health` endpoint printed by the sample app. Packages currently contain core identity/record types and build markers, not functioning middleware or storage.

## NestJS workspace

Requires a recent Node 24 LTS, npm, and an internet connection for dependency installation:

~~~bash
cd typescript
npm install
npm run build
npm test
npm run start:orders
~~~

Open `http://localhost:3000/health`. The npm workspaces include `@idempotencykit/core`, `@idempotencykit/nestjs`, `@idempotencykit/postgres` and the sample `@idempotencykit/orders-api`.

### Lockfile policy

No npm lockfile was generated for the initial commit because registry access was not available in the authoring environment. After `npm install` succeeds, commit `typescript/package-lock.json`, then change the CI install step to `npm ci` for repeatability.

## Development checkpoints

- [x] Create a .NET 10 solution with core, ASP.NET Core, PostgreSQL, tests, and sample projects.
- [x] Create TypeScript/npm workspaces, NestJS 12 host, and basic scaffold tests.
- [x] Add scaffold CI configuration.
- [ ] Observe successful `dotnet build` and `dotnet test` runs.
- [ ] Observe successful `npm run build` and `npm test` runs.
- [ ] Build and verify the actual idempotency engine and PostgreSQL integration.

## First feature milestone

Implement a bounded, scoped, atomic PostgreSQL claim and safe same-request response replay in one framework, with failure tests. Port the **same HTTP contract** to the other framework rather than independently inventing API behavior.

See [Roadmap](ROADMAP.md), [Guarantees](../spec/GUARANTEES.md), and [Failure Matrix](../spec/FAILURE_MATRIX.md).
