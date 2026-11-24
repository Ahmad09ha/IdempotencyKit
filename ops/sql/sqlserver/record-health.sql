-- Read-only aggregate; run with a monitoring identity. No keys or replay bytes.
DECLARE @observed_at datetime2(3) = SYSUTCDATETIME();
WITH classified AS (
    SELECT CASE
        WHEN state = 'uncertain' THEN 'uncertain'
        WHEN state = 'processing' AND (lease_expires_at IS NULL OR lease_expires_at <= @observed_at) THEN 'processing_expired'
        WHEN state = 'processing' THEN 'processing_active'
        WHEN state = 'completed' AND expires_at IS NULL THEN 'invalid_state'
        WHEN state = 'completed' AND expires_at <= @observed_at THEN 'completed_expired'
        WHEN state = 'completed' THEN 'completed_retained'
        ELSE 'invalid_state'
    END AS bucket,
        CASE WHEN created_at > @observed_at THEN CAST(0 AS bigint)
             ELSE DATEDIFF_BIG(second, created_at, @observed_at) END AS age_seconds
    FROM dbo.idempotencykit_records
)
SELECT bucket, COUNT_BIG(*) AS record_count, MAX(age_seconds) AS oldest_age_seconds
FROM classified GROUP BY bucket ORDER BY bucket;
