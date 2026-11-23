# Identity and request fingerprint v1 — IK-CJSON/1

**Implementation milestone:** standalone deterministic hashing in the .NET core library and TypeScript core package. This is **not** a request middleware/interceptor, database claim, replay mechanism, or transaction safety feature.

## APIs

- .NET: `RequestIdentityHasher.ComputeScopeKey(IdempotencyRequestScope)` and `RequestFingerprinter.Compute(RequestFingerprintInput)`.
- TypeScript: `computeScopeKey(scope)` and `computeRequestFingerprint(request)` exported from `@idempotencykit/core`.
- Both produce `v1:` followed by 64 lowercase SHA-256 hex digits.
- The **same** golden test vectors under `test-vectors/fingerprint-v1.json` are executed independently by both packages.

## Scope key

The internal lookup key is the SHA-256 hash of this **UTF-8 encoded canonical JSON array**, with no whitespace:

~~~text
["idempotencykit.scope.v1", namespace, operationId, tenantId|null, principalId, clientKey]
~~~

Each string uses the IK-CJSON/1 quoting rule described below. Each field is positional and domain-separated by the first string. The optional tenant is JSON `null` if absent; an empty tenant ID is distinct. A scope key is not the fingerprint: it represents **which logical operation key within an authenticated scope** is being consulted.

The scope must be derived from **trusted server-side principal and tenant data**; clients can supply only the idempotency key. Client keys must be 1–255 ASCII characters from `A-Z`, `a-z`, `0-9`, `.`, `_`, `:`, `-`. At least one character is required. Other scope strings must not be empty or only whitespace. Hashing identifiers avoids storing raw values in the lookup key, but is **not anonymization** or authorization.

## Request fingerprint

The input comprises method, stable operation ID, canonical path (starting `/` without query/fragment), decoded query entries, and the **original JSON body text**. A missing body is different from JSON literal `null`.

The fingerprint is the SHA-256 hash of this UTF-8 canonical JSON array:

~~~text
[
  "idempotencykit.request.v1",
  UPPERCASE_ASCII_METHOD,
  operationId,
  path,
  [[queryName,queryValue],...],
  ["absent"] or ["json",canonicalJsonBody]
]
~~~

The actual preimage is minified; the multiline format above is illustrative. Query pairs are sorted by the decoded name and then value, both by UTF-16 ordinal code units. Duplicate query entries are **preserved**. If query entry order is business-significant, v1's sorting policy is **not appropriate**; use a future operation-specific fingerprint policy.

Identity does not include the client key, tenant or principal inside the request fingerprint: those are used by the **separate scope hash**.

## IK-CJSON/1 JSON canonicalization

This is a **custom deterministic profile, not RFC 8785/JCS**. It is designed specifically to match across C# and JavaScript while preserving exact JSON decimal values.

1. Parse a single valid JSON value with no comments/trailing commas. Reject duplicate object member names, including names that become equal after interpreting escapes.
2. Object properties are sorted by **UTF-16 code-unit ordinal** order (not culture order). Array order remains significant.
3. Encode strings as quoted JSON with ordinary printable ASCII characters untouched except `"` and `\`, which are escaped. Controls and all non-ASCII UTF-16 code units are encoded with lowercase `\uXXXX`. Surrogate pairs are escaped as two `\uXXXX` units; unpaired surrogates are rejected.
4. Booleans are `true`/`false`; `null` is `null`.
5. Numbers are normalized **exactly as decimal values**, without IEEE-754 rounding: remove leading zeroes in the coefficient; remove trailing zeroes while incrementing the base-10 exponent; use `digits` optionally followed by `e` and a signed decimal exponent (no plus sign). All signed zero forms become `0`. For example, `1000` and `1e3` canonicalize to `1e3`, and `19.90` canonicalizes to `199e-1`. Fractions such as `0.00123` canonicalize to `123e-5`.
6. Maximum raw JSON body is 64 KiB UTF-8, maximum object/array nesting is 32, and absolute base-10 exponent limit is 1,000,000. The initial implementation is optimized for small JSON REST commands, **not arbitrary large request bodies**.
7. Input must be the original JSON text. **Never stringify already-parsed JavaScript numbers** before hashing; large integers and decimal lexemes could already have lost precision.

The implementation uses .NET `JsonDocument` raw numeric lexemes and JavaScript `JSON.parse` with reviver `context.source` (requires sufficiently recent Node.js 22/24). It does not treat JSON body fields as trusted business identifiers; auth and validation still run outside this utility.

## Hash examples

For the JSON body `{"productId":50,"quantity":2}` and `{"quantity":2,"productId":50}`, the fingerprint is identical:

`v1:0340c4f5b85123546da9f236704a0d58e840f9f0ef18554bdd9457d56baf7ce4`

Changing quantity to 3 changes the fingerprint to:

`v1:d68d9b688377ba92ad3b476faeb0b5c709c117ce4ec8e83523399406902d5185`

The full request and other test fixtures are recorded in `test-vectors/fingerprint-v1.json`. These values are **not client-side security signatures**.

## Compatibility and limitations

- Canonical input construction is a **protocol**. Changing the order, field inclusion, escaping, or number representation changes existing hashes. New behavior must use a new version prefix/domain label.
- Server-supplied path normalization, API-version selection, proxy rewriting, and selection of relevant query/body fields will need explicit adapter policy. Hash the *same* normalized values in all instances.
- This v1 algorithm includes the *whole JSON body* and all query entries; it is not an endpoint-specific configurable extractor.
- This is not RFC 8785, and it does not apply Unicode normalization (NFC/NFD). Visually similar but different Unicode strings produce different hashes.
- Tests must run under **real .NET and Node** runtimes and CI before compatibility is called verified.
- Next milestone: PostgreSQL unique claims, state transitions, lease/recovery policy, response persistence and replay.

This document defines only the **hashing and scope foundation**; it does not promise idempotent HTTP behavior by itself.
