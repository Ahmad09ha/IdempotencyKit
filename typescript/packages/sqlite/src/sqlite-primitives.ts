import { DatabaseSync } from "node:sqlite";
import type { StatementSync } from "node:sqlite";
import { resolve } from "node:path";
import type { StoredHttpResponse } from "@idempotencykit/core";

export const SQLITE_NOW_MS = "CAST(unixepoch('subsec') * 1000 AS INTEGER)";
export const MAX_LEASE_MS = 86_400_000;
export const MAX_RETENTION_MS = 365 * MAX_LEASE_MS;
const VALID_HASH = /^v1:[0-9a-f]{64}$/;
const SAFE_HEADERS = new Set(["location", "etag", "content-language"]);
const UTF8 = new TextDecoder("utf-8", { fatal: true });

export function sqlitePath(file: string): string {
  if (typeof file !== "string" || !file.trim() ||
      file === ":memory:" || /^file:/i.test(file))
    throw new Error("SQLite v1 requires a LOCAL database path, never :memory: or URI.");
  return resolve(file);
}
export function validateHash(hash: string): void {
  if (typeof hash !== "string" || !VALID_HASH.test(hash))
    throw new Error("Expected lowercase v1: SHA-256 digest.");
}
export function validateDuration(value: number, max: number): void {
  if (!Number.isSafeInteger(value) || value < 1 || value > max)
    throw new RangeError("Duration must be integer milliseconds within provider bounds.");
}
export function validateOwner(owner: string): void {
  if (typeof owner !== "string" ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(owner))
    throw new Error("Valid UUID owner token required.");
}
export function validateBusyTimeout(ms: number): void {
  validateDuration(ms, 30_000);
}

export function openSqliteDatabase(
  fullPath: string, busyTimeoutMs: number,
): DatabaseSync {
  validateBusyTimeout(busyTimeoutMs);
  const db = new DatabaseSync(fullPath, { timeout: busyTimeoutMs });
  try {
    db.exec("PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;");
    const value = db.prepare("PRAGMA journal_mode").get() as {
      journal_mode?: string;
    } | undefined;
    if (value?.journal_mode?.toLowerCase() !== "wal")
      throw new Error("SQLite idempotency requires WAL; initialize the file first.");
    return db;
  } catch (error) {
    db.close();
    throw error;
  }
}

const localGates = new Map<string, Promise<void>>();

/**
 * The built-in Node SQLite driver is synchronous. Serialize all this package's
 * operations on the SAME absolute database file within a process so an async
 * transactional callback cannot cause a second same-process connection to
 * block the event loop with a busy timeout while the first awaits.
 * Other processes still contend through SQLite's real file locks.
 */
export async function withLocalFileGate<T>(
  file: string, work: () => Promise<T>,
): Promise<T> {
  const predecessor = localGates.get(file) ?? Promise.resolve();
  let finish!: () => void;
  const current = new Promise<void>(resolve => { finish = resolve; });
  localGates.set(file, current);
  await predecessor;
  try { return await work(); }
  finally {
    finish();
    if (localGates.get(file) === current) localGates.delete(file);
  }
}

/**
 * Restricted transactional statement session; no BEGIN/COMMIT/ROLLBACK,
 * PRAGMA or database attachment through this API. Never expose DatabaseSync
 * directly to a user callback because it could commit the idempotency claim
 * before the response is validated.
 */
export interface SqliteTransactionSession {
  prepare(sql: string): StatementSync;
}
export function makeSession(db: DatabaseSync): SqliteTransactionSession {
  return Object.freeze({
    prepare(sql: string): StatementSync {
      if (typeof sql !== "string" || !sql.trim() ||
          /^\s*(?:BEGIN|COMMIT|END|ROLLBACK|SAVEPOINT|RELEASE|ATTACH|DETACH|PRAGMA|VACUUM)\b/i
            .test(sql))
        throw new Error("Transaction/connection control statements are forbidden.");
      return db.prepare(sql);
    },
  });
}

export type SqliteRow = {
  request_fingerprint: string;
  state: "processing" | "completed" | "uncertain";
  lease_expires_at: number | null;
  expired: number | null;
  response_status: number | null;
  response_content_type: string | null;
  response_body: Uint8Array | null;
  response_headers: string | null;
};

export const READ_SQL =
  "SELECT request_fingerprint, state, lease_expires_at, " +
  "lease_expires_at <= " + SQLITE_NOW_MS + " AS expired, " +
  "response_status, response_content_type, response_body, response_headers " +
  "FROM idempotencykit_records WHERE scope_key_hash = ?";

export function prepareResponse(response: StoredHttpResponse): {
  headersJson: string; response: StoredHttpResponse;
} {
  if (!response || !Number.isSafeInteger(response.statusCode) ||
      response.statusCode < 200 || response.statusCode > 299)
    throw new Error("Only 2xx response status codes can be persisted.");
  if (!(response.body instanceof Uint8Array) ||
      response.body.byteLength > 262_144)
    throw new Error("JSON body must be <= 256 KiB.");
  if (typeof response.contentType !== "string" ||
      response.contentType.length > 128 || /[\r\n]/.test(response.contentType) ||
      !["application/json", "application/problem+json"].includes(
        response.contentType.split(";")[0]!.trim().toLowerCase()))
    throw new Error("Only safe JSON content types are supported.");
  JSON.parse(UTF8.decode(response.body));
  if (!response.headers || Array.isArray(response.headers) ||
      typeof response.headers !== "object")
    throw new Error("Response headers must be a map.");
  const headers: Record<string, string> = Object.create(null) as Record<string, string>;
  for (const [original, value] of Object.entries(response.headers)) {
    const name = original.toLowerCase();
    if (!SAFE_HEADERS.has(name) || Object.hasOwn(headers, name))
      throw new Error("Unsafe or duplicate response header.");
    if (typeof value !== "string" || value.length > 4096 || /[\r\n]/.test(value))
      throw new Error("Unsafe response header value.");
    headers[name] = value;
  }
  return { headersJson: JSON.stringify(headers),
    response: { ...response, headers } };
}

export function readResponse(row: SqliteRow): StoredHttpResponse {
  if (row.response_status === null || row.response_content_type === null ||
      row.response_body === null || row.response_headers === null)
    throw new Error("Incomplete stored SQLite response.");
  const headers: unknown = JSON.parse(row.response_headers);
  if (!headers || Array.isArray(headers) || typeof headers !== "object")
    throw new Error("Corrupt stored response header object.");
  const response: StoredHttpResponse = {
    statusCode: row.response_status,
    contentType: row.response_content_type,
    body: new Uint8Array(row.response_body),
    headers: headers as Record<string, string>,
  };
  return prepareResponse(response).response;
}
