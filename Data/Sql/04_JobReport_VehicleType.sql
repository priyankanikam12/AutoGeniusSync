-- DMS_JobReport: Cogxim "vehicleType" field (e.g. Internal / External). Safe to re-run.
IF COL_LENGTH(N'dbo.DMS_JobReport', N'VehicleType') IS NULL
    ALTER TABLE dbo.DMS_JobReport ADD VehicleType NVARCHAR(100) NULL;
GO
