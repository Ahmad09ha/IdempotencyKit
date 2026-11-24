-- Read-only aggregate; run with a monitoring identity. No keys or replay bytes.
WITH observed AS (SELECT statement_timestamp() AS observed_at),
classified AS (
    SELECT CASE
        WHEN state = 'uncertain' THEN 'uncertain'
        WHEN state = 'processing' AND (lease_expires_at IS NULL OR lease_expires_at <= observed_at) THEN 'processing_expired'
        WHEN state = 'processing' THEN 'processing_active'
        WHEN state = 'completed' AND expires_at IS NULL THEN 'invalid_state'
        WHEN state = 'completed' AND expires_at <= observed_at THEN 'completed_expired'
        WHEN state = 'completed' THEN 'completed_retained'
        ELSE 'invalid_state'
    END AS bucket,
        GREATEST(0, FLOOR(EXTRACT(EPOCH FROM (observed_at - created_at)))) AS age_seconds
    FROM idempotencykit_records CROSS JOIN observed
)
SELECT bucket, COUNT(*) AS record_count, MAX(age_seconds) AS oldest_age_seconds
FROM classified GROUP BY bucket ORDER BY bucket;
