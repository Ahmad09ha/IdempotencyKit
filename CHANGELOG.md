# Changelog

## Unreleased — 0.1.0-preview.1 candidate

- Two independent implementations of scoped request hashes, Standard-mode HTTP, and explicit transaction-based HTTP APIs.
- PostgreSQL, SQL Server 2022 and single-host local SQLite: store/executor, ASP.NET Core and NestJS Express transactional adapters, and provider-specific .NET EF Core 10 bridges.
- Preview build/pack and independent package install checks for .NET 10 and Node 24; this is not a published release.
- Runnable SQLite Orders demos, implementation walkthrough and operations guide.
- Reliability verification covers real Kestrel and NestJS process-crash cases, both runtimes cooperating on the same local SQLite file and shared PostgreSQL/SQL Server HTTP replay, contention and mixed-runtime recovery. Linux SQLite permission faults and safe retry are tested. See the detailed [verification evidence](docs/VERIFICATION.md) for tested boundaries.

The existing versioned database migrations are unchanged. Embedded or packaged migrations are not applied automatically on package installation. Preview API compatibility may change; use explicit version pins when testing.
