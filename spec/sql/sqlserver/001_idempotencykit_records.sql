-- IdempotencyKit SQL Server 2022+/Azure SQL provider schema v1.
-- Run from a migration identity; application role needs SELECT/INSERT/UPDATE/
-- DELETE on this table and EXECUTE sys.sp_getapplock in this database.
-- Apply to the SAME database as transactional business tables.
-- No GO client commands: runnable through SqlCommand or node-mssql.

IF OBJECT_ID(N'dbo.idempotencykit_records', N'U') IS NULL
BEGIN
    EXEC(N'
    CREATE TABLE dbo.idempotencykit_records
    (
        scope_key_hash nvarchar(67) COLLATE Latin1_General_100_BIN2 NOT NULL
            CONSTRAINT PK_idempotencykit_records PRIMARY KEY,
        request_fingerprint nvarchar(67) COLLATE Latin1_General_100_BIN2 NOT NULL,
        state varchar(12) NOT NULL
            CONSTRAINT CK_idempotencykit_state CHECK
                (state IN (''processing'', ''completed'', ''uncertain'')),
        owner_token uniqueidentifier NULL,
        lease_expires_at datetime2(3) NULL,
        response_status int NULL,
        response_content_type nvarchar(128) NULL,
        response_body varbinary(max) NULL,
        response_headers nvarchar(max) NULL,
        created_at datetime2(3) NOT NULL
            CONSTRAINT DF_idempotencykit_created DEFAULT SYSUTCDATETIME(),
        completed_at datetime2(3) NULL,
        expires_at datetime2(3) NULL,
        CONSTRAINT CK_idempotencykit_completed CHECK
            (state <> ''completed'' OR
                (owner_token IS NULL AND lease_expires_at IS NULL AND
                 response_status BETWEEN 200 AND 299 AND
                 response_content_type IS NOT NULL AND
                 response_body IS NOT NULL AND
                 response_headers IS NOT NULL AND
                 ISJSON(response_headers) = 1 AND
                 DATALENGTH(response_body) <= 262144 AND
                 completed_at IS NOT NULL AND expires_at IS NOT NULL))
    );
    ');
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.idempotencykit_records')
      AND name = N'IX_idempotencykit_expiry'
)
BEGIN
    EXEC(N'CREATE INDEX IX_idempotencykit_expiry
           ON dbo.idempotencykit_records(state, expires_at)
           WHERE expires_at IS NOT NULL');
END;
