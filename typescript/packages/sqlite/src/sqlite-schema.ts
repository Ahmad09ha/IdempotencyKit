import { DatabaseSync } from "node:sqlite";
import { sqlitePath } from "./sqlite-primitives.js";

/** Mirrors spec/sql/sqlite/001_idempotencykit_records.sql. */
export const SQLITE_SCHEMA_V1 = `
CREATE TABLE IF NOT EXISTS idempotencykit_records (
    scope_key_hash TEXT COLLATE BINARY NOT NULL PRIMARY KEY
      CHECK (length(scope_key_hash) = 67 AND substr(scope_key_hash,1,3) = 'v1:'),
    request_fingerprint TEXT COLLATE BINARY NOT NULL
      CHECK (length(request_fingerprint) = 67 AND substr(request_fingerprint,1,3) = 'v1:'),
    state TEXT NOT NULL CHECK (state IN ('processing','completed','uncertain')),
    owner_token TEXT,
    lease_expires_at INTEGER,
    response_status INTEGER,
    response_content_type TEXT,
    response_body BLOB,
    response_headers TEXT,
    created_at INTEGER NOT NULL DEFAULT (CAST(unixepoch('subsec') * 1000 AS INTEGER)),
    completed_at INTEGER,
    expires_at INTEGER,
    CHECK (
      state <> 'completed'
      OR (
        owner_token IS NULL AND lease_expires_at IS NULL AND
        response_status BETWEEN 200 AND 299 AND
        response_content_type IS NOT NULL AND response_body IS NOT NULL AND
        length(response_body) <= 262144 AND
        response_headers IS NOT NULL AND json_valid(response_headers) = 1 AND
        completed_at IS NOT NULL AND expires_at IS NOT NULL
      )
    )
);
CREATE INDEX IF NOT EXISTS ix_idempotencykit_expiry
    ON idempotencykit_records(expires_at)
    WHERE state = 'completed' AND expires_at IS NOT NULL;
`;

export function initializeSqliteDatabase(databasePath: string): void {
  const fullPath = sqlitePath(databasePath);
  const db = new DatabaseSync(fullPath, { timeout: 5_000 });
  try {
    db.exec("PRAGMA journal_mode = WAL;");
    const mode = db.prepare("PRAGMA journal_mode").get() as
      { journal_mode?: string } | undefined;
    if (mode?.journal_mode?.toLowerCase() !== "wal")
      throw new Error("Could not enable SQLite WAL on local file.");
    db.exec("PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;");
    db.exec(SQLITE_SCHEMA_V1);
  } finally {
    db.close();
  }
}
