# Learning Guide: What We Want to Learn by Building IdempotencyKit

IdempotencyKit is a **learning-driven infrastructure project**, not just middleware. The value is in understanding why the common shortcut fails, proving the boundaries of a better approach, and packaging the result for repeated use in future applications.

## Study sequence

### 1. HTTP and client retries

- Why do clients retry a POST after a timeout?
- Difference between a repeated **network request** and the same **logical business command**.
- What is the difference between an idempotent operation, safe HTTP method, and a retryable request?
- Exercise: make an Orders endpoint commit a row, then close the connection before responding. Observe the duplicate.

### 2. Atomicity, uniqueness and concurrency

- Unique constraints vs “check whether key exists, then insert.”
- Transactions, isolation levels, lock waits, deadlocks and retry policy.
- Exercise: send 100 parallel requests using the same key; demonstrate a SELECT-before-INSERT race, then fix it with a unique constraint.

### 3. Distributed coordination

- At-least-once delivery, retries, timeouts, leased ownership and fencing.
- Why an expired lease cannot prove that a paused process stopped producing effects.
- Exercise: pause one request past its lease, allow a second worker to acquire ownership, then resume the first. Verify which updates are fenced and what remains unsafe.

### 4. HTTP response capture and replay

- Status codes, JSON serialization, response headers and content negotiation.
- Why replaying a raw cookie or streaming response is unsafe.
- Exercise: compare the actual wire response with the handler return object in ASP.NET Core and NestJS.

### 5. Cross-language contracts

- Designing one behavior contract with two independent implementations.
- JSON canonicalization, Unicode, decimal precision and versioned fingerprints.
- Exercise: feed identical fixtures into C# and TypeScript and demand byte-identical fingerprints.

### 6. Transactional idempotency

- Same-database transactions vs two independent writes.
- Ordering of intent record, business effects, stored response and commit.
- Exercise: kill the process immediately before commit and immediately after commit; show that the retry outcome is safe and different.

### 7. Shipping infrastructure packages

- NuGet package layout, ASP.NET Core extension points and dependency injection.
- npm workspaces, NestJS module/decorator/interceptor design, peer dependencies.
- Semantic versioning, compatibility, migrations, CI and reproducible local labs.
- Exercise: install each package into a **different** sample application without importing the package source code directly.

## Engineering questions to answer in every PR

1. Which real failure does this change address?
2. Which invariant is preserved, and where does it stop applying?
3. Could two processes run this code concurrently?
4. What happens if the process dies on each side of a commit?
5. What is still unverified?
6. Can both languages demonstrate the same user-visible behavior?

## Recommended resources

- [HTTP Idempotency-Key working draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/)
- [PostgreSQL: Transaction Isolation](https://www.postgresql.org/docs/current/transaction-iso.html)
- [PostgreSQL: Unique Indexes](https://www.postgresql.org/docs/current/indexes-unique.html)
- [RFC 8785: JSON Canonicalization](https://www.rfc-editor.org/rfc/rfc8785)
- [ASP.NET Core filters](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/filters)
- [NestJS interceptors](https://docs.nestjs.com/interceptors)
- [ReliableFlow example](https://github.com/Ahmad09ha/ReliableFlow)

The goal is to graduate from “this seems to work” to **“we can state exactly what is guaranteed, under which assumptions, and demonstrate it with a failure test.”**
