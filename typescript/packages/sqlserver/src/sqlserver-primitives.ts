import sql from "mssql";
import type { StoredHttpResponse } from "@idempotencykit/core";

const SHA256 = /^v1:[a-f0-9]{64}$/;
const SAFE_HEADERS = new Set(["location", "etag", "content-language"]);
const MAX_RESPONSE_BYTES = 262_144;
export const MAX_LEASE_MS = 86_400_000;
export const MAX_RETENTION_MS = 365 * MAX_LEASE_MS;

export type ExistingRow = {
  request_fingerprint: string;
  state: "processing" | "uncertain" | "completed";
  lease_expired: boolean | null;
  response_status: number | null;
  response_content_type: string | null;
  response_body: Uint8Array | Buffer | null;
  response_headers: string | null;
};

export function validateHash(value: string): void {
  if (typeof value !== "string" || !SHA256.test(value))
    throw new Error("Expected a v1: SHA-256 hash.");
}

export function validateDuration(ms: number, maximum: number): void {
  if (!Number.isSafeInteger(ms) || ms < 1 || ms > maximum)
    throw new RangeError("Duration must be an integer millisecond count within bounds.");
}

export function durationParams(request: sql.Request, prefix: string, duration: number): sql.Request {
  validateDuration(duration, MAX_RETENTION_MS);
  return request
    .input(prefix + "_seconds", sql.Int, Math.floor(duration / 1000))
    .input(prefix + "_milliseconds", sql.Int, duration % 1000);
}

/**
 * Acquire a per-scoped-key SQL Server transaction-owned application lock.
 * -1 timeout is safe to classify as same-key InProgress. Other negative codes
 * (canceled, deadlock victim, parameter error) must be surfaced as errors.
 */
export async function acquireKeyLock(
  transaction: sql.Transaction, scopeKeyHash: string, timeoutMs: number,
): Promise<boolean> {
  validateHash(scopeKeyHash);
  validateDuration(timeoutMs, 30_000);
  const result = await new sql.Request(transaction)
    .input("resource", sql.NVarChar(255), "IdempotencyKit:v1:" + scopeKeyHash)
    .input("wait", sql.Int, timeoutMs)
    .query(`
      DECLARE @code int;
      EXEC @code = sys.sp_getapplock
        @Resource = @resource, @LockMode = 'Exclusive',
        @LockOwner = 'Transaction', @LockTimeout = @wait;
      SELECT @code AS lock_code;`);
  const code = result.recordset?.[0]?.lock_code;
  if (typeof code !== "number")
    throw new Error("SQL Server did not return application-lock status.");
  if (code >= 0) return true;
  if (code === -1) return false;
  throw new Error("SQL Server key lock failed with code " + code + ".");
}

export const INSPECT_SQL = `
  SELECT request_fingerprint, state,
         CAST(CASE WHEN lease_expires_at <= SYSUTCDATETIME()
                   THEN 1 ELSE 0 END AS bit) AS lease_expired,
         response_status, response_content_type, response_body,
         response_headers
    FROM dbo.idempotencykit_records WHERE scope_key_hash = @key`;

export function prepareResponse(response: StoredHttpResponse): {
  headersJson: string; response: StoredHttpResponse;
} {
  if (!response || !Number.isInteger(response.statusCode) ||
      response.statusCode < 200 || response.statusCode > 299)
    throw new Error("Only 2xx responses may be persisted.");
  if (!(response.body instanceof Uint8Array) ||
      response.body.byteLength > MAX_RESPONSE_BYTES)
    throw new Error("Response must contain <=256 KiB of bytes.");
  if (typeof response.contentType !== "string" ||
      response.contentType.length > 128 ||
      /[\r\n]/.test(response.contentType) ||
      !["application/json", "application/problem+json"].includes(
        response.contentType.split(";")[0]!.trim().toLowerCase()))
    throw new Error("Only safe JSON content types may be persisted.");
  if (!response.headers || Array.isArray(response.headers) ||
      typeof response.headers !== "object")
    throw new Error("Response header object required.");

  const json = new TextDecoder("utf-8", { fatal: true }).decode(response.body);
  JSON.parse(json);

  const headers: Record<string, string> = Object.create(null) as Record<string, string>;
  for (const [original, value] of Object.entries(response.headers)) {
    const key = original.toLowerCase();
    if (!SAFE_HEADERS.has(key) || Object.hasOwn(headers, key))
      throw new Error("Unsafe/duplicate response header.");
    if (typeof value !== "string" || value.length > 4096 || /[\r\n]/.test(value))
      throw new Error("Unsafe response header value.");
    headers[key] = value;
  }

  return {
    headersJson: JSON.stringify(headers),
    response: { ...response, headers },
  };
}

export function readResponse(row: ExistingRow): StoredHttpResponse {
  if (!Number.isInteger(row.response_status) ||
      row.response_status === null || row.response_content_type === null ||
      row.response_body === null || row.response_headers === null)
    throw new Error("Incomplete stored SQL Server replay response.");
  const headers: unknown = JSON.parse(row.response_headers);
  if (!headers || typeof headers !== "object" || Array.isArray(headers))
    throw new Error("Invalid stored response header map.");
  return {
    statusCode: row.response_status!,
    contentType: row.response_content_type,
    body: new Uint8Array(row.response_body),
    headers: headers as Record<string, string>,
  };
}
