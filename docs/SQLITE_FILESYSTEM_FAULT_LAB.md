# SQLite filesystem permission fault lab

The Linux CI exercises an actual OS permission failure against a disposable local SQLite file, not a mocked exception or a production disk. Both .NET TestServer and NestJS Express use their real transactional HTTP adapter.

1. Commit an order and retain its response.
2. Close per-operation database connections, deny writes to the fixture database and its directory, and prove OS-level write denial.
3. Verify a new request and an existing-key replay both fail closed with HTTP 503 (`IDEMPOTENCY_TRANSACTION_UNAVAILABLE`); no additional business callback runs.
4. Restore the original permissions in a finally block, verify the previously committed data and replay bytes are intact, and retry the originally rejected new key.
5. Verify exactly one new business effect and completed replay record, with identical bytes and Location on repeat.

The test is explicitly skipped for root and non-Linux environments; only a non-skipped result on an unprivileged Linux runner constitutes evidence. The tests modify permissions on their own temporary fixture only. SQLite WAL may need directory and WAL/shared-memory file permissions, depending on open mode; see [SQLite WAL read-only rules](https://www.sqlite.org/wal.html#read_only_databases).

This lab is not a physical disk exhaustion, power failure, filesystem corruption, Windows ACL, NFS/SMB or database failover certification. Do not simulate disk failure against a production filesystem. Test results are pending CI at implementation time.
