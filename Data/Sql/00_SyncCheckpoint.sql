-- AutoGeniusSync — checkpoint table + UniqueKey constraints.
-- Safe to run more than once. The application runs the same statements at start-up (Data/SyncSchema.cs).
-- Database: DMS_IOT_DATA (SQL Server).

IF OBJECT_ID(N'dbo.DMS_SyncCheckpoint', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DMS_SyncCheckpoint
    (
        Id               BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_DMS_SyncCheckpoint PRIMARY KEY,
        ApiName          NVARCHAR(50)   NOT NULL,
        DealerCode       NVARCHAR(50)   NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_DealerCode DEFAULT (N''),
        PeriodStart      DATE           NOT NULL,
        PeriodEnd        DATE           NOT NULL,
        Status           NVARCHAR(20)   NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Status DEFAULT (N'Pending'),
        Attempts         INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Attempts DEFAULT (0),
        RecordsFetched   INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Fetched DEFAULT (0),
        RecordsInserted  INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Inserted DEFAULT (0),
        RecordsUpdated   INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Updated DEFAULT (0),
        RecordsUnchanged INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Unchanged DEFAULT (0),
        RecordsSkipped   INT            NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_Skipped DEFAULT (0),
        LastError        NVARCHAR(2000) NULL,
        StartedAt        DATETIME2      NULL,
        CompletedAt      DATETIME2      NULL,
        CreatedAt        DATETIME2      NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt        DATETIME2      NOT NULL CONSTRAINT DF_DMS_SyncCheckpoint_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_DMS_SyncCheckpoint_Api_Dealer_Period'
               AND object_id = OBJECT_ID(N'dbo.DMS_SyncCheckpoint'))
    CREATE UNIQUE INDEX UQ_DMS_SyncCheckpoint_Api_Dealer_Period
        ON dbo.DMS_SyncCheckpoint (ApiName, DealerCode, PeriodStart);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DMS_SyncCheckpoint_Api_Status'
               AND object_id = OBJECT_ID(N'dbo.DMS_SyncCheckpoint'))
    CREATE INDEX IX_DMS_SyncCheckpoint_Api_Status
        ON dbo.DMS_SyncCheckpoint (ApiName, Status, PeriodStart);
GO

-- Unique filtered indexes on UniqueKey (duplicate prevention at database level).
-- If one of these fails with error 1505, the table already contains duplicates: run 01_DuplicateCheck.sql first.

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.DMS_ServiceHistory') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [UQ_DMS_ServiceHistory_UniqueKey] ON dbo.[DMS_ServiceHistory] (UniqueKey) WHERE UniqueKey IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.DMS_VehicleSales') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [UQ_DMS_VehicleSales_UniqueKey] ON dbo.[DMS_VehicleSales] (UniqueKey) WHERE UniqueKey IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.DMS_VehicleDispatches') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [UQ_DMS_VehicleDispatches_UniqueKey] ON dbo.[DMS_VehicleDispatches] (UniqueKey) WHERE UniqueKey IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.DMS_LineOrderReport') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [UQ_DMS_LOR_UniqueKey] ON dbo.[DMS_LineOrderReport] (UniqueKey) WHERE UniqueKey IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
               JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE i.object_id = OBJECT_ID(N'dbo.DMS_JobReport') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [UX_DMS_JobReport_UniqueKey] ON dbo.[DMS_JobReport] (UniqueKey) WHERE UniqueKey IS NOT NULL;
GO

-- Progress view: one line per API — how many months x dealers are done / pending / failed.
-- SELECT ApiName, Status, COUNT(*) AS Periods, SUM(RecordsFetched) AS Fetched,
--        SUM(RecordsInserted) AS Inserted, SUM(RecordsUpdated) AS Updated
-- FROM dbo.DMS_SyncCheckpoint GROUP BY ApiName, Status ORDER BY ApiName, Status;
