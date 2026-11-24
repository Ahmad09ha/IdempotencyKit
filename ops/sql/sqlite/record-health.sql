-- Read-only aggregate; open the local file in read-only mode. No keys or replay bytes.
WITH observed AS (SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER) AS observed_at),
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
        MAX(0, CAST((observed_at - created_at) / 1000 AS INTEGER)) AS age_seconds
    FROM idempotencykit_records CROSS JOIN observed
)
SELECT bucket, COUNT(*) AS record_count, MAX(age_seconds) AS oldest_age_seconds
FROM classified GROUP BY bucket ORDER BY bucket;
