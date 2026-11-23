-- IdempotencyKit Standard-mode store (PostgreSQL 15+).
-- Apply with a managed migration before starting the app. Never run arbitrary DDL
-- in each incoming HTTP request. This migration is intentionally idempotent for
-- development; production migration tools must track executed revisions.
CREATE TABLE IF NOT EXISTS idempotencykit_records (
    scope_key_hash varchar(67) PRIMARY KEY
        CHECK (scope_key_hash ~ '^v1:[0-9a-f]{64}$'),
    request_fingerprint varchar(67) NOT NULL
        CHECK (request_fingerprint ~ '^v1:[0-9a-f]{64}$'),
    state varchar(12) NOT NULL
        CHECK (state IN ('processing','completed','uncertain')),
    owner_token uuid,
    lease_expires_at timestamptz,
    response_status integer,
    response_content_type text,
    response_body bytea,
    response_headers jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz,
    expires_at timestamptz,
    CONSTRAINT ik_records_state_shape CHECK (
        (state = 'processing'
            AND owner_token IS NOT NULL AND lease_expires_at IS NOT NULL
            AND response_status IS NULL AND response_content_type IS NULL
            AND response_body IS NULL AND response_headers IS NULL
            AND completed_at IS NULL AND expires_at IS NULL)
        OR (state = 'uncertain'
            AND owner_token IS NULL AND lease_expires_at IS NULL
            AND response_status IS NULL AND response_content_type IS NULL
            AND response_body IS NULL AND response_headers IS NULL
            AND completed_at IS NULL AND expires_at IS NULL)
        OR (state = 'completed'
            AND owner_token IS NULL AND lease_expires_at IS NULL
            AND response_status BETWEEN 200 AND 299
            AND response_content_type IS NOT NULL
            AND response_body IS NOT NULL
            AND octet_length(response_body) <= 262144
            AND response_headers IS NOT NULL
            AND jsonb_typeof(response_headers) = 'object'
            AND completed_at IS NOT NULL AND expires_at IS NOT NULL
            AND expires_at > completed_at)
    )
);
CREATE INDEX IF NOT EXISTS ik_records_completed_expiry
    ON idempotencykit_records (expires_at)
    WHERE state = 'completed';
