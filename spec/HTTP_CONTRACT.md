# IdempotencyKit — Proposed HTTP Contract (v1)

> **Status: a v1 subset is implemented in Standard-mode HTTP integrations.** The remaining decisions in this document are design proposals, not verified production features. The draft is informed by the IETF [Idempotency-Key draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/), **not** a final RFC. See [the implemented v1 behavior](../docs/HTTP_STANDARD_V1.md); error-body normalization and general 4xx/5xx replay are still future work.

## Scope

The initial target is authenticated JSON REST **write** endpoints (normally POST; PATCH/PUT may opt in). A protected endpoint declares a stable **operation ID** and whether the key is required. General GET requests should not need idempotency records.

### Client-supplied header

~~~http
Idempotency-Key: order-abc-123
~~~

- One newly generated unpredictable key **per intended logical operation**; reuse it for all retries of that operation.
- Reusing a key with changed relevant request content is a client error.
- Configuration will bound key size and character set. The exact default validation constraints must be finalized before release.
- The service does not silently invent a new key for a required-key endpoint.
- A completed response is replayable only within the documented retention window.

## Scoped identity

A storage identity is derived from the tuple:

~~~text
(service namespace, operation ID, tenant ID, authenticated principal ID, client key)
~~~

A deployment may use a tenant-only or application-client scope where appropriate, but **never** a global untrusted client key with no authorization boundary. Identity values are combined using an unambiguous length-prefixed or structured encoding and cryptographically hashed for indexes. Store hashes, not raw secrets or bearer tokens.

Authorizing the original request is not sufficient to authorize **replay**: authentication and the currently applicable authorization checks execute before returning stored data. Do not expose results across tenant/user boundaries.

## Request fingerprint

The fingerprint is a hash of the **relevant logical request**, including:

1. Stable operation identity and API contract version.
2. HTTP method, canonical route/path parameters and relevant normalized query values.
3. Canonicalized JSON request body.
4. Other relevant client-controlled request fields **explicitly selected** for this operation.

Token contents, trace IDs, transient headers, and timestamps unrelated to business meaning should not be fingerprint inputs. The spec must version the fingerprint algorithm and test it with shared fixtures.

**Canonical JSON is not trivial across languages.** RFC 8785 is a candidate; numerical precision, duplicate JSON object keys, Unicode, nulls, and arrays need documented behavior and test vectors. Reject unsupported/unbounded payloads instead of silently hashing incompatible representations. A byte-for-byte SHA-256 hash of raw JSON is **not** the same as semantic JSON equality.

A completed or in-progress identity with a *different* fingerprint must be rejected, not replayed or silently treated as a new operation.

## Observable responses

| Condition | Proposed HTTP response | Must execute handler? |
| --- | --- | --- |
| First valid key / no existing claim | Normal endpoint response | Yes, one eligible owner |
| Completed key + same fingerprint | Original supported status, body and safe headers | **No** |
| Completed or processing key + different fingerprint | `422 Unprocessable Content` | **No** |
| Processing key + same fingerprint | `409 Conflict` with bounded retry guidance when appropriate | **No** |
| Missing/invalid key when required | `400 Bad Request` | **No** |
| Dependency/storage unavailable | `503 Service Unavailable` | **No** |
| Outcome unknown after Standard-mode lease loss | Explicit uncertain/reconciliation response (exact status to finalize) | **No by default** |

A replay must not create a fresh resource. Returning `201 Created` on replay is acceptable because it represents the **original** response; it is not a special “201 Replayed” status.

For errors, use an RFC 9457-style `application/problem+json` document with machine-readable application code (e.g. `IDEMPOTENCY_KEY_MISMATCH`). The exact error schema, retry hints, and replay indicator header remain to be frozen.

### Response recording

The first version should support only bounded, ordinary JSON responses. Store the response status, serialized body, content type, and a **small allowlist** of response headers (for example `Location` when safe). Do **not** blindly replay `Set-Cookie`, hop-by-hop headers, authorization artifacts, dynamic security policy headers, or outdated `Content-Length`.

Only intentionally chosen terminal outcomes are cacheable. Validation errors, handler exceptions, and unexpected server errors need a documented policy distinguishing safe-to-retry from unknown business effects. In Transactional mode, persist the exact supported response representation **before committing the shared business transaction**.

## Key retention

An initial example retention value is 24 hours after successful completion. This is a **policy setting, not a guarantee or standard default**. Eviction before the declared window undermines deduplication. Once a key expires, the service may legitimately treat it as new. Longer business uniqueness requires independent domain constraints.

## Implementation ordering

1. Authenticate, establish tenant/principal and authorize access.
2. Validate key and request constraints.
3. Derive scoped identity and fingerprint.
4. Atomically claim or inspect the record.
5. If completed and matching, authorize replay and emit recorded response.
6. If mismatched/in-flight/uncertain, return the corresponding safe result.
7. If newly claimed, execute under the selected reliability mode.
8. Complete the record with ownership validation (Standard) or as part of the shared transaction (Transactional).
9. Emit the supported response; never release unsafe unknown work for blind execution.

## Out-of-scope for initial v1

Streaming/SSE, raw/manual response manipulation, multipart bodies, arbitrary large responses, WebSockets, arbitrary external-service calls, and exactly-once effects beyond the relevant database.

## Open questions to decide before code freeze

- Exact key constraints and header parsing rules.
- Fingerprint canonicalization and body-size limits across .NET/JS runtimes.
- Stable request identity for path/version/query combinations.
- Error schema and uncertain outcome HTTP code.
- Which 4xx/5xx responses are retained and which require reconciliation.
- Replay indicator and tracing/metrics header conventions.
- Retention and replay authorization policy when account permissions change.
