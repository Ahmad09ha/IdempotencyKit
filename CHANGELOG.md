# Changelog

## Unreleased — 0.1.0-preview.1 candidate

- Two independent implementations of scoped request hashes, Standard-mode HTTP, and explicit transaction-based HTTP APIs.
- PostgreSQL, SQL Server 2022 and single-host local SQLite: store/executor, ASP.NET Core and NestJS Express transactional adapters, and provider-specific .NET EF Core 10 bridges.
- Preview build/pack and independent package install checks for .NET 10 and Node 24; this is not a published release.
- Runnable SQLite Orders demos, implementation walkthrough and operations guide.
- SQLite verification covers both runtimes cooperating on one local file and separate HTTP process-crash cases. See the detailed [verification evidence](docs/VERIFICATION.md) for tested boundaries.

The existing versioned database migrations are unchanged. Embedded or packaged migrations are not applied automatically on package installation. Preview API compatibility may change; use explicit version pins when testing.
