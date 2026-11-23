import { randomUUID } from "node:crypto";
import type { Pool, QueryResultRow } from "pg";
import type {
  IdempotencyClaimResult, IdempotencyStore, StoredHttpResponse,
} from "@idempotencykit/core";

const HASH = /^v1:[a-f0-9]{64}$/;
const SAFE_HEADERS = new Set(["location", "etag", "content-language"]);
const MAX_BODY_BYTES = 262_144;
const ONE_DAY_MS = 86_400_000;
const ONE_YEAR_MS = 365 * ONE_DAY_MS;

type ExistingRow = QueryResultRow & {
  request_fingerprint: string;
  state: "processing" | "completed" | "uncertain";
  lease_expired: boolean | null;
  response_status: number | null;
  response_content_type: string | null;
  response_body: Buffer | null;
  response_headers: string | null;
};

/**
 * Standard-mode PostgreSQL store with atomic insert, owner-token fencing,
 * fail-closed expired leases and completed-response replay data.
 * NO HTTP interceptor and NO cross-transaction business safety yet.
 *
 * Apply spec/sql/postgres/001_idempotencykit_records.sql before use.
 */
export class PostgresIdempotencyStore implements IdempotencyStore {
  constructor(private readonly pool: Pool) {}

  async tryClaim(
    scopeKeyHash: string, requestFingerprint: string, leaseMs: number,
  ): Promise<IdempotencyClaimResult> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateMs(leaseMs, ONE_DAY_MS);

    for (let attempt = 0; attempt < 3; attempt++) {
      const ownerToken = randomUUID();
      const inserted = await this.pool.query(
        `INSERT INTO idempotencykit_records
          (scope_key_hash, request_fingerprint, state, owner_token, lease_expires_at)
         VALUES ($1, $2, 'processing', $3::uuid,
                 clock_timestamp() + ($4::double precision * interval '1 millisecond'))
         ON CONFLICT (scope_key_hash) DO NOTHING
         RETURNING owner_token`,
        [scopeKeyHash, requestFingerprint, ownerToken, leaseMs],
      );

      if (inserted.rowCount === 1) return { status: "acquired", ownerToken };

      let record = await this.read(scopeKeyHash);
      if (!record) continue;
      if (record.request_fingerprint !== requestFingerprint)
        return { status: "fingerprint_mismatch" };

      if (record.state === "processing" && record.lease_expired) {
        const changed = await this.pool.query(
          `UPDATE idempotencykit_records
              SET state = 'uncertain', owner_token = NULL, lease_expires_at = NULL
            WHERE scope_key_hash = $1 AND request_fingerprint = $2
              AND state = 'processing' AND lease_expires_at <= clock_timestamp()`,
          [scopeKeyHash, requestFingerprint],
        );
        if (changed.rowCount === 1) return { status: "uncertain" };
        record = await this.read(scopeKeyHash);
        if (!record) continue;
        if (record.request_fingerprint !== requestFingerprint)
          return { status: "fingerprint_mismatch" };
      }

      if (record.state === "processing") return { status: "in_progress" };
      if (record.state === "uncertain") return { status: "uncertain" };
      if (record.state === "completed") return {
        status: "completed", response: readResponse(record),
      };
      throw new Error("Unexpected idempotency record state.");
    }

    throw new Error("Idempotency record changed during claim; retry later.");
  }

  async tryComplete(
    scopeKeyHash: string, requestFingerprint: string,
    ownerToken: string, response: StoredHttpResponse, retentionMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateHash(requestFingerprint);
    validateUuid(ownerToken);
    validateMs(retentionMs, ONE_YEAR_MS);
    const headers = JSON.stringify(validateResponse(response));

    const result = await this.pool.query(
      `UPDATE idempotencykit_records
          SET state = 'completed',
              owner_token = NULL,
              lease_expires_at = NULL,
              response_status = $4,
              response_content_type = $5,
              response_body = $6,
              response_headers = $7::jsonb,
              completed_at = clock_timestamp(),
              expires_at = clock_timestamp() +
                  ($8::double precision * interval '1 millisecond')
        WHERE scope_key_hash = $1 AND request_fingerprint = $2
          AND state = 'processing' AND owner_token = $3::uuid
          AND lease_expires_at > clock_timestamp()`,
      [
        scopeKeyHash, requestFingerprint, ownerToken,
        response.statusCode, response.contentType, Buffer.from(response.body),
        headers, retentionMs,
      ],
    );
    return result.rowCount === 1;
  }

  async tryExtendLease(
    scopeKeyHash: string, ownerToken: string, leaseMs: number,
  ): Promise<boolean> {
    validateHash(scopeKeyHash);
    validateUuid(ownerToken);
    validateMs(leaseMs, ONE_DAY_MS);

    const result = await this.pool.query(
      `UPDATE idempotencykit_records
          SET lease_expires_at =
            clock_timestamp() + ($3::double precision * interval '1 millisecond')
        WHERE scope_key_hash = $1 AND state = 'processing'
          AND owner_token = $2::uuid AND lease_expires_at > clock_timestamp()`,
      [scopeKeyHash, ownerToken, leaseMs],
    );
    return result.rowCount === 1;
  }

  async purgeExpiredCompleted(batchSize: number): Promise<number> {
    if (!Number.isSafeInteger(batchSize) || batchSize < 1 || batchSize > 1000)
      throw new RangeError("Batch size must be between 1 and 1000.");

    const result = await this.pool.query(
      `WITH expired AS (
         SELECT scope_key_hash FROM idempotencykit_records
          WHERE state = 'completed' AND expires_at <= clock_timestamp()
          ORDER BY expires_at LIMIT $1 FOR UPDATE SKIP LOCKED
       )
       DELETE FROM idempotencykit_records r USING expired e
        WHERE r.scope_key_hash = e.scope_key_hash`,
      [batchSize],
    );
    return result.rowCount ?? 0;
  }

  private async read(scopeKeyHash: string): Promise<ExistingRow | undefined> {
    const result = await this.pool.query<ExistingRow>(
      `SELECT request_fingerprint, state,
              lease_expires_at <= clock_timestamp() AS lease_expired,
              response_status, response_content_type, response_body,
              response_headers::text
         FROM idempotencykit_records WHERE scope_key_hash = $1`,
      [scopeKeyHash],
    );
    return result.rows[0];
  }
}

function readResponse(row: ExistingRow): StoredHttpResponse {
  if (row.response_status === null || row.response_content_type === null ||
      row.response_body === null || row.response_headers === null)
    throw new Error("Incomplete stored completed response.");
  const headers: unknown = JSON.parse(row.response_headers);
  if (headers === null || typeof headers !== "object" || Array.isArray(headers))
    throw new Error("Invalid stored response header data.");
  return {
    statusCode: row.response_status,
    contentType: row.response_content_type,
    body: new Uint8Array(row.response_body),
    headers: headers as Record<string, string>,
  };
}

function validateHash(value: string): void {
  if (typeof value !== "string" || !HASH.test(value))
    throw new Error("Expected a v1: SHA-256 hash.");
}

function validateUuid(value: string): void {
  if (typeof value !== "string" ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value))
    throw new Error("Expected a non-empty UUID owner token.");
}

function validateMs(value: number, maximum: number): void {
  if (!Number.isSafeInteger(value) || value < 1 || value > maximum)
    throw new RangeError("Duration must be an integer in milliseconds within supported bounds.");
}

function validateResponse(response: StoredHttpResponse): Record<string, string> {
  if (!response || !Number.isInteger(response.statusCode) ||
      response.statusCode < 200 || response.statusCode > 299)
    throw new Error("Only 2xx completed responses can be stored.");
  if (!(response.body instanceof Uint8Array) || response.body.byteLength > MAX_BODY_BYTES)
    throw new Error("Response body exceeds 256 KiB or is invalid.");
  if (typeof response.contentType !== "string" ||
      !["application/json", "application/problem+json"].includes(
        response.contentType.split(";")[0]!.trim().toLowerCase()))
    throw new Error("Only JSON responses can be stored.");
  if (response.headers === null || typeof response.headers !== "object" ||
      Array.isArray(response.headers))
    throw new Error("Response headers must be an object.");

  const safe: Record<string, string> = Object.create(null) as Record<string, string>;
  for (const [original, value] of Object.entries(response.headers)) {
    const name = original.toLowerCase();
    if (!SAFE_HEADERS.has(name) || Object.hasOwn(safe, name))
      throw new Error("Unsafe or duplicate response header: " + original);
    if (typeof value !== "string" || value.length > 4096 || /[\r\n]/.test(value))
      throw new Error("Unsafe response header value.");
    safe[name] = value;
  }
  return safe;
}
