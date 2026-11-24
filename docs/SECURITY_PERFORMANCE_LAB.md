# Security and performance hardening — phase 2

## Scope and security invariants

- Validate replayable JSON bytes (strict UTF-8, valid JSON, <=256 KiB), status, content type and allowlisted headers before persistence, and revalidate completed records before returning them. A malformed replay must never execute the operation again. PostgreSQL previously validated headers on write but did not fully revalidate completed rows on read; its Standard store did not parse response JSON. SQL Server and SQLite had read validation but did not reject all forbidden field-value controls.
- Reject HTTP field values containing forbidden C0 controls or DEL (HTAB allowed), including NUL, before committing a business transaction. RFC 9110 §5.5: https://www.rfc-editor.org/rfc/rfc9110.html#section-5.5 . Tests deliberately insert malformed fixture data into disposable databases, not production. Existing valid fingerprints, schema and API signatures are unchanged.
- Negative tests run the real Standard store and Transactional executor on all three providers in both languages: invalid header/content type/JSON/UTF-8 cannot commit; successful business INSERT followed by invalid capture must roll back both records; tampered completed data must fail closed without invoking the callback.
- ASP.NET Core Standard response capture bounds `WriteByte` and `SetLength` as well as ordinary writes. Request/response limits do not replace upstream parser limits, concurrency/rate limits, quotas, or application-level authorization (OWASP API4: https://api-security.owasp.org/editions/2023/en/0xa4-unrestricted-resource-consumption/).
- Scope: trusted app callbacks and authenticated principals. A malicious callback with raw DB transaction access, an attacker with database write access able to forge *valid* records, or application authorization misconfiguration is not prevented by replay validation. No claim of encryption, production certification or external exactly-once semantics.

Evidence is pending exact-source CI at implementation time. Performance baselines are a separate work package on this branch; no throughput or latency guarantee is inferred from correctness tests.
