-- AutoGeniusSync — READ-ONLY duplicate check. Changes nothing.
-- Lists rows that share the same business key, so they can be reviewed before any clean-up.

-- 1. Same UniqueKey more than once (only possible if the unique index is missing)
SELECT 'DMS_ServiceHistory' AS TableName, UniqueKey, COUNT(*) AS Copies FROM dbo.DMS_ServiceHistory
 WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL
SELECT 'DMS_VehicleSales', UniqueKey, COUNT(*) FROM dbo.DMS_VehicleSales
 WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL
SELECT 'DMS_VehicleDispatches', UniqueKey, COUNT(*) FROM dbo.DMS_VehicleDispatches
 WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL
SELECT 'DMS_LineOrderReport', UniqueKey, COUNT(*) FROM dbo.DMS_LineOrderReport
 WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1
UNION ALL
SELECT 'DMS_JobReport', UniqueKey, COUNT(*) FROM dbo.DMS_JobReport
 WHERE UniqueKey IS NOT NULL GROUP BY UniqueKey HAVING COUNT(*) > 1;

-- 2. VehicleSales / VehicleDispatches: the old ERP sync wrote rows WITHOUT a UniqueKey (matched on ChassisNo only),
--    while the bulk push APIs write rows WITH one. The same chassis can therefore exist twice.
--    The new sync adopts the legacy (UniqueKey IS NULL) row instead of inserting a new one, but rows that
--    are ALREADY duplicated are listed here for review.
SELECT 'DMS_VehicleSales' AS TableName, UPPER(LTRIM(RTRIM(ChassisNo))) AS Chassis, COUNT(*) AS Copies,
       SUM(CASE WHEN UniqueKey IS NULL THEN 1 ELSE 0 END) AS WithoutKey
FROM dbo.DMS_VehicleSales WHERE ChassisNo IS NOT NULL
GROUP BY UPPER(LTRIM(RTRIM(ChassisNo))) HAVING COUNT(*) > 1;

SELECT 'DMS_VehicleDispatches' AS TableName, UPPER(LTRIM(RTRIM(ChassisNo))) AS Chassis, COUNT(*) AS Copies,
       SUM(CASE WHEN UniqueKey IS NULL THEN 1 ELSE 0 END) AS WithoutKey
FROM dbo.DMS_VehicleDispatches WHERE ChassisNo IS NOT NULL
GROUP BY UPPER(LTRIM(RTRIM(ChassisNo))) HAVING COUNT(*) > 1;
