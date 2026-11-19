using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Data;

// ─────────────────────────────────────────────────────────────
// Idempotent schema additions for the checkpoint-based sync.
//
// The app creates its database with EnsureCreatedAsync(), which does NOTHING when the database
// already exists — so a new table added to AppDbContext would never be created on the live server.
// This runs at start-up (Program.cs) and is safe to run on every start:
//   1. creates DMS_SyncCheckpoint (+ its unique key) if it does not exist
//   2. makes sure the UNIQUE filtered indexes on UniqueKey exist on the 4 ERP report tables and on
//      DMS_JobReport (they are in the EF model, but EnsureCreated never adds them to an existing
//      database). If a table already contains duplicate UniqueKey values the index cannot be
//      created — that is logged with the table name and the start-up continues; run
//      Data/Sql/01_DuplicateCheck.sql to list the duplicates.
// The same statements are in Data/Sql/00_SyncCheckpoint.sql for a DBA to run by hand.
// ─────────────────────────────────────────────────────────────
public static class SyncSchema
{
    private const string CreateCheckpointTable = @"
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
        ON dbo.DMS_SyncCheckpoint (ApiName, Status, PeriodStart);";

    // (table, index name) — unique filtered index on UniqueKey. Same names as in AppDbContext.
    private static readonly (string Table, string Index)[] UniqueKeyIndexes =
    {
        ("DMS_ServiceHistory",    "UQ_DMS_ServiceHistory_UniqueKey"),
        ("DMS_VehicleSales",      "UQ_DMS_VehicleSales_UniqueKey"),
        ("DMS_VehicleDispatches", "UQ_DMS_VehicleDispatches_UniqueKey"),
        ("DMS_LineOrderReport",   "UQ_DMS_LOR_UniqueKey"),
        ("DMS_JobReport",         "UX_DMS_JobReport_UniqueKey"),
    };

    public static async Task EnsureAsync(AppDbContext db, ILogger logger, IConfiguration config, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(CreateCheckpointTable, ct);
        logger.LogInformation("DMS_SyncCheckpoint table verified.");

        // Sync is dealer-wise only now; "all dealers" checkpoint rows (DealerCode = '') are progress records of
        // a mode that crashes the ERP. Removing them deletes no business data — those months are covered by
        // the per-dealer rows, which are fetched in full.
        if (!config.GetValue("HistoricalSync:AllowAllDealerCalls", false))
        {
            var n = await db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.DMS_SyncCheckpoint WHERE DealerCode = N'' AND ApiName NOT LIKE N'FY[_]%'", ct);
            if (n > 0) logger.LogInformation("{n} obsolete all-dealer checkpoint row(s) removed (sync is dealer-wise).", n);
        }

        foreach (var (table, index) in UniqueKeyIndexes)
        {
            // Any unique index on UniqueKey (whatever its name) counts — do not create a second one.
            var sql = $@"
IF OBJECT_ID(N'dbo.{table}', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.{table}', N'UniqueKey') IS NOT NULL
   AND NOT EXISTS (
        SELECT 1 FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(N'dbo.{table}') AND i.is_unique = 1 AND c.name = N'UniqueKey')
    CREATE UNIQUE INDEX [{index}] ON dbo.[{table}] (UniqueKey) WHERE UniqueKey IS NOT NULL;";
            try
            {
#pragma warning disable EF1002 // table/index names come from the fixed list above, never from input
                await db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
            }
            catch (Exception ex)
            {
                // Typical cause: duplicate UniqueKey values already in the table (SQL error 1505).
                logger.LogError(
                    "Could not create unique index {index} on {table}: {msg}. Duplicate UniqueKey rows probably exist — " +
                    "run Data/Sql/01_DuplicateCheck.sql. Sync continues; duplicates are still prevented in code.",
                    index, table, ex.InnerException?.Message ?? ex.Message);
            }
        }
    }
}
