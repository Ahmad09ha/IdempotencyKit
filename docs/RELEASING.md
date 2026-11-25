# Build, verify and publish IdempotencyKit

This repository produces **11 NuGet packages for .NET 10** and **eight npm packages for Node 24 (ES modules)**. NestJS HTTP packages target NestJS 12 on Express. Select just the library and provider you use; do not publish the root workspace or either Orders sample. The first candidate version is `0.1.0-preview.1`, not a stable release.

## 1. Preview without publishing

From the repository root, with .NET 10, Node 24, npm and Python 3 installed:

~~~bash
dotnet pack dotnet/IdempotencyKit.slnx -c Release -o artifacts/nuget
python3 scripts/verify-nuget-packages.py artifacts/nuget

cd typescript
npm ci
npm run build
npm run pack:smoke
~~~

Use a fresh NuGet output directory per version. The independent consumer tests install compiled artifacts from outside the source workspaces. They check all package IDs and public entry points, internal references and declarations, and run a file-backed SQLite claim. The package CI job runs this same gate without any registry credentials. The provider integration workflows separately exercise PostgreSQL, SQL Server 2022 and SQLite. No smoke job authenticates to or publishes on a registry.

For an npm tarball you can inspect yourself after the build, run `npm pack -w @idempotencykit/nestjs-sqlite` in `typescript/`. The workspace package is still private and can be packed but not published. Public artifacts include JavaScript, TypeScript declarations and a README; provider-core artifacts include the versioned SQL migration. NuGet artifacts include the matching DLL/XML docs, README and provider-core migration. Build the artifacts from the reviewed release commit and record that commit with the release.

## 2. Distribution preflight (no publish)

The existing Package Preview workflow now retains the **same** eight npm tarballs that passed the isolated install/type/runtime smoke check, alongside the eleven verified NuGet packages. The `distribution-preflight` job reads those artifacts, verifies the package family/version, records the checkout commit, package metadata and SHA-256 hashes, and uploads `release-manifest.json`, `SHA256SUMS` and `registry-preflight.json`. Download the three artifacts from the same run and keep the `nuget/` and `npm/` paths when checking hashes. These are MIT-licensed preview build artifacts, not published registry releases. The archive gate verifies identical license text and SPDX metadata on all 19 artifacts.

The registry report queries public metadata only, with no credentials. A 404 means no public package was found, **not** that the name is free or the scope is owned. An existing name or candidate version needs owner/immutability verification; unknown/network responses need rechecking. Nothing is staged, reserved, published, tagged or granted write access.

## 3. Decide what gets released

- MIT is the maintainer-approved repository license. The [root LICENSE](../LICENSE), npm library manifests and their lockfile metadata, and the NuGet SPDX expression must agree. Each artifact includes the root license text. Third-party dependencies retain their own licenses; review their terms before release.
- Own the package names on nuget.org and the `@idempotencykit` npm account/organization scope. The scope is not created by GitHub and may not be available. If a different scope is needed, rename package names, internal dependency keys and imports together, regenerate the lockfile, and retest.
- Have an authorized maintainer approve the contents, security findings and release commit. Keep packages labeled preview while compatibility/production gates are open. Document database migrations and changes in the changelog. One release version spans the package family: set the .NET source version in `dotnet/src/Directory.Build.props` and keep all eight npm versions and their internal dependency versions in sync; also update sample references, regenerate `typescript/package-lock.json`, and rerun all CI and standalone smoke checks.
- Only after approval remove `"private": true` from the eight npm **library** manifests. Leave it on the root workspace and sample, and keep .NET tests/samples non-packable. Pack again, review the actual archive, and freeze the version. Registry versions are immutable: fix mistakes in a new version.

## 4. Publish NuGet (.NET)

Use a NuGet.org account that owns the package IDs. For a first manual preview, create a short-lived API key with only the required Push permissions and package glob (a sufficiently narrow prefix glob for new IDs). Store it in a shell secret/environment variable, not source or chat. The public options are described in [NuGet’s publishing guide](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package). Prefer [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) on a future approved release workflow.

Publish in dependency order:

1. `IdempotencyKit.Core`.
2. `IdempotencyKit.AspNetCore` and the three provider cores `IdempotencyKit.Postgres`, `IdempotencyKit.SqlServer`, `IdempotencyKit.Sqlite`.
3. The three `IdempotencyKit.EntityFrameworkCore.<Provider>` and three `IdempotencyKit.AspNetCore.<Provider>` packages.

For each reviewed artifact, starting with core:

~~~bash
dotnet nuget push artifacts/nuget/IdempotencyKit.Core.0.1.0-preview.1.nupkg \
  --source https://api.nuget.org/v3/index.json --api-key "$NUGET_API_KEY"
~~~

Wait for validation/indexing and confirm the version on NuGet.org before installing in a clean app. In Visual Studio or Rider's NuGet UI, select nuget.org and enable prerelease packages. The CLI equivalent for SQLite HTTP is:

~~~bash
dotnet package add IdempotencyKit.AspNetCore.Sqlite --version 0.1.0-preview.1
~~~

For EF Core, also add the separate `IdempotencyKit.EntityFrameworkCore.Sqlite` package if needed. PostgreSQL/SQL Server have equivalent packages. For Standard mode, choose `IdempotencyKit.AspNetCore` and the desired provider core instead. Run the provider migration using your deployment process; package installation alone is not a database migration.

## 5. Publish npm (NestJS)

Use an npm account with publish access to the scope. Use npm login with required 2FA for interactive releases, or configure [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/) for an approved workflow where available. No shared secret belongs in source. The details for a first scoped package are in [npm's scoped public package guide](https://docs.npmjs.com/creating-and-publishing-scoped-public-packages/).

From `typescript/`, after building and approving the manifests/archives, dry-run the intended command for every library first:

~~~bash
npm publish -w @idempotencykit/core --access public --tag next --dry-run
~~~

Publish in order: `core`; then `postgres`, `sqlserver`, `sqlite`, `nestjs`; then `nestjs-postgres`, `nestjs-sqlserver`, `nestjs-sqlite`. For example:

~~~bash
npm publish -w @idempotencykit/core --access public --tag next
~~~

Use `--access public` for the scoped packages and `--tag next` so a preview is not the default `latest`. Verify the version exists before proceeding to packages that depend on it:

~~~bash
npm view @idempotencykit/core@0.1.0-preview.1 version
~~~

Verify in a fresh NestJS 12 + Express project. For SQLite HTTP:

~~~bash
npm install @idempotencykit/nestjs-sqlite@0.1.0-preview.1
~~~

The framework peer dependencies belong to the host app; provider core and internal dependencies are installed from npm. Add the provider core as a direct dependency if your application also imports it to initialize the database. For Standard mode, use `@idempotencykit/nestjs` and the desired database provider. Follow the [integration examples](GETTING_STARTED.md) to wire trusted app authentication, the required raw JSON body and PostgreSQL/SQL Server migrations or SQLite initialization.

**This guide does not certify production deployment.** Keep unsupported adapters/topologies out of release claims, including Fastify and SQLite on a shared network filesystem. See [verification and limits](VERIFICATION.md).

## 6. Trusted publishing — maintainer setup, not enabled

The current plan is an approved manual preview, then GitHub-hosted OIDC publication restricted to the release workflow/environment. Do not enable write credentials on PR, build, or inventory jobs.

| Registry | Required owner-side setup |
| --- | --- |
| NuGet | Sign in at nuget.org, select the intended individual/organization owner, create the trusted publishing policy for GitHub owner `Ahmad09ha`, repo `IdempotencyKit`, the final approved workflow filename and environment. Permit only the needed new-package/new-version operations and narrow package scope. The eventual publish job uses `NuGet/login` to exchange OIDC for a short-lived API key. |
| npm | Create/verify access to the `@idempotencykit` user/org scope. First publication requires registry-side authorization; use the current first-package procedure and 2FA. For existing packages, configure trusted publishers for each package, exact repository/workflow/environment and permitted publish action. Use a supported npm CLI (11.5.1+) and Node 24 on a hosted runner; keep provenance enabled and prefer prerelease tag `next`. |

Sources: [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), [npm trusted publishers](https://docs.npmjs.com/trusted-publishers/), [npm scoped public packages](https://docs.npmjs.com/creating-and-publishing-scoped-public-packages/). Recheck registry procedures when activating because they can change. No token, password, 2FA code or private credential belongs in source or chat. Do not claim ownership from public metadata. No actual publish workflow, tag or policy is activated until the license, package ownership and release approval are settled.

## 7. License decision (approved)

The maintainer explicitly approved MIT on 2026-11-25. Original project code and documentation are licensed under the [MIT License](../LICENSE) with the collective copyright notice `Copyright (c) 2026 IdempotencyKit contributors`, consistent with the existing package author metadata. MIT allows commercial use, modification and redistribution subject to retaining the copyright and permission notices; it disclaims warranties. It does not relicense third-party dependencies or imply that third-party permissions or independent security review have been audited.

## 8. Registry name collision — publication blocked

The [verified public registry preflight](https://github.com/Ahmad09ha/IdempotencyKit/actions/runs/45614664107) reported one existing public NuGet ID, `IdempotencyKit.AspNetCore`; the other ten NuGet IDs and all eight npm names were not publicly found. None of the candidate versions were found. The registry metadata alone does not identify who can publish. Verify the existing ID under the intended account, or approve a new package identity/namespace strategy. Do not publish a subset, overwrite an existing version, or silently change package IDs. The npm scope also requires actual account/organization ownership. Keep library `private: true`, registry writes disabled, and version `0.1.0-preview.1` until these decisions and release gates are resolved. The project name and all APIs remain unchanged.
