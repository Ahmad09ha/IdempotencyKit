# Relational HTTP crash lab (release hardening)

This is a test-only harness for PostgreSQL 17 and SQL Server 2022. It does not change public package APIs or claim production failover certification. See the dedicated [Relational HTTP Crash Recovery CI](../.github/workflows/relational-crash-ci.yml) for observed results; the following describes the acceptance criteria, not a claim that the tests have passed.

## Fault model and assertions

- Launch a real, loopback-only Kestrel child process with the actual provider HTTP adapter and synthetic test authentication. Never use that authentication in an application.
- Before COMMIT: pause the callback after its business INSERT in the executor-owned transaction. Independently read committed state, verify neither the business order nor the idempotency record is visible, and hard-kill the process.
- After COMMIT, before HTTP headers: the real adapter returns a 201 only after the transaction completes. Pause in ASP.NET Core `HttpResponse.OnStarting` and verify both committed records and the recorded response bytes are visible from a different connection. Hard-kill the process before the client sees headers.
- Start a fresh Kestrel process on the same database. Retry the same scoped key; check the 201, original recorded bytes and Location, one durable business row, one completed record, repeated replay, changed payload 422 and unauthenticated replay 401.
- SQL Server uses a randomly named disposable database with READ_COMMITTED_SNAPSHOT enabled to observe committed state while a transaction is blocked. The test never uses dirty reads. This setting is for the lab, not an application configuration requirement.
- The PostgreSQL lab requires a separate database named `idk_crash`. SQL Server requires a disposable instance with permission to create/drop a uniquely named fixture database. Never run these tests against production. The workflow provisions isolated containers.

## Boundaries

An application process kill is not a database-server crash, power loss, disk failure, ambiguous database COMMIT acknowledgement or failover test. The tests do not claim exactly-once effects outside the shared transaction. This is one slice of the release-hardening milestone; the existing SQLite preview work and cross-runtime relational workloads require separate verification.

References: [ASP.NET Core 10 OnStarting](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.httpresponse.onstarting?view=aspnetcore-10.0), [SQL Server isolation levels](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql), [PostgreSQL 17 transaction isolation](https://www.postgresql.org/docs/17/transaction-iso.html).
