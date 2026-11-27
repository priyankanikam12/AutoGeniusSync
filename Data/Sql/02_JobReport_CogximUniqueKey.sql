-- AutoGeniusSync — add Cogxim's own job-card id to DMS_JobReport.
-- RUN THIS BEFORE deploying the new build (the app reads/writes this column).
IF COL_LENGTH(N'dbo.DMS_JobReport', N'CogximUniqueKey') IS NULL
    ALTER TABLE dbo.DMS_JobReport ADD CogximUniqueKey NVARCHAR(50) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DMS_JobReport_CogximUniqueKey'
               AND object_id = OBJECT_ID(N'dbo.DMS_JobReport'))
    CREATE INDEX IX_DMS_JobReport_CogximUniqueKey ON dbo.DMS_JobReport (CogximUniqueKey);
GO
