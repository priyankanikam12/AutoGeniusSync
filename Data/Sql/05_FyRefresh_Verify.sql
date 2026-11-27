-- Verify the 12 AM current-FY refresh. Run (1) BEFORE 12 AM and again AFTER the run shows Completed; compare.
DECLARE @today DATE = CAST(SWITCHOFFSET(SYSUTCDATETIME(), '+05:30') AS DATE);
DECLARE @to DATE = DATEADD(DAY, -1, @today);
DECLARE @fy DATE = DATEFROMPARTS(CASE WHEN MONTH(@to) >= 4 THEN YEAR(@to) ELSE YEAR(@to) - 1 END, 4, 1);
SELECT @fy AS FyStart, @to AS FyTo;

-- 1) Row counts: current FY (replaced nightly) vs older years (must NOT change)
SELECT 'ServiceHistory' AS TableName,
       SUM(CASE WHEN JobDate >= @fy AND JobDate <= @to THEN 1 ELSE 0 END) AS CurrentFyRows,
       SUM(CASE WHEN JobDate < @fy THEN 1 ELSE 0 END) AS OlderRows,
       SUM(CASE WHEN JobDate >= @fy AND RepairType IS NOT NULL THEN 1 ELSE 0 END) AS FyRowsWithRepairType
FROM dbo.DMS_ServiceHistory
UNION ALL SELECT 'VehicleSales', SUM(CASE WHEN InvoiceDate >= @fy AND InvoiceDate <= @to THEN 1 ELSE 0 END), SUM(CASE WHEN InvoiceDate < @fy THEN 1 ELSE 0 END), NULL FROM dbo.DMS_VehicleSales
UNION ALL SELECT 'VehicleDispatches', SUM(CASE WHEN InvoiceDate >= @fy AND InvoiceDate <= @to THEN 1 ELSE 0 END), SUM(CASE WHEN InvoiceDate < @fy THEN 1 ELSE 0 END), NULL FROM dbo.DMS_VehicleDispatches
UNION ALL SELECT 'LineOrderReport', SUM(CASE WHEN JobDate >= @fy AND JobDate <= @to THEN 1 ELSE 0 END), SUM(CASE WHEN JobDate < @fy THEN 1 ELSE 0 END),
       SUM(CASE WHEN JobDate >= @fy AND MaterialCode IS NOT NULL THEN 1 ELSE 0 END) FROM dbo.DMS_LineOrderReport;

-- 2) Run status (expect Status = Completed, SwappedTables = all 4)
SELECT TOP 5 Id, RunDate, FyStart, ToDate, Status, SwappedTables, StartedAt, CompletedAt, Notes
FROM dbo.DMS_FyRefreshState ORDER BY Id DESC;

-- 3) ERP units of the run (expect only Completed; Failed rows show LastError)
SELECT ApiName, Status, COUNT(*) AS Units, SUM(CAST(RecordsFetched AS BIGINT)) AS RowsFetched
FROM dbo.DMS_SyncCheckpoint WHERE ApiName LIKE N'FY[_]%' GROUP BY ApiName, Status ORDER BY ApiName, Status;
SELECT TOP 20 ApiName, DealerCode, PeriodStart, Attempts, LastError
FROM dbo.DMS_SyncCheckpoint WHERE ApiName LIKE N'FY[_]%' AND Status <> N'Completed' ORDER BY UpdatedAt DESC;

-- 4) Duplicates (expect 0 rows)
SELECT 'ServiceHistory' t, UniqueKey, COUNT(*) n FROM dbo.DMS_ServiceHistory WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL SELECT 'VehicleSales', UniqueKey, COUNT(*) FROM dbo.DMS_VehicleSales WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL SELECT 'VehicleDispatches', UniqueKey, COUNT(*) FROM dbo.DMS_VehicleDispatches WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL SELECT 'LineOrderReport', UniqueKey, COUNT(*) FROM dbo.DMS_LineOrderReport WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1;

-- 5) Last FY-refresh log lines
SELECT TOP 10 Id, SyncType, Status, StartedAt, CompletedAt, RecordsFetched, RecordsInserted, ErrorMessage
FROM dbo.DMS_SyncLog WHERE SyncType = N'FyRefresh' ORDER BY Id DESC;
