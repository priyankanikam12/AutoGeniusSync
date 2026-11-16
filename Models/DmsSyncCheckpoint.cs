namespace AutoGeniusSync.Models;

// ─────────────────────────────────────────────────────────────
// DMS_SyncCheckpoint — database-backed progress of the month-wise, dealer-wise ERP sync.
//
// One row per (ApiName, DealerCode, PeriodStart). PeriodStart is always the 1st of a month,
// PeriodEnd the last day of that month. DealerCode = "" means "all dealers in one call"
// (only used when HistoricalSync:PerDealer = false).
//
// Status:
//   Pending     – not fetched yet
//   InProgress  – a run started it; if the process died (IIS recycle / stop / reboot) the row
//                 stays InProgress and the next run simply processes it again (UPSERT is idempotent,
//                 so a half-written month can never create duplicates)
//   Completed   – ERP answered and every row of the month was written; set ONLY after the
//                 database insert/update committed. RecordsFetched = 0 means "ERP had no data".
//   Failed      – ERP/DB error; retried automatically on the next run (see Attempts / LastError)
// ─────────────────────────────────────────────────────────────
public class DmsSyncCheckpoint
{
    public long Id { get; set; }
    public string ApiName { get; set; } = "";
    public string DealerCode { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Status { get; set; } = SyncCheckpointStatus.Pending;
    public int Attempts { get; set; }
    public int RecordsFetched { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsUnchanged { get; set; }
    public int RecordsSkipped { get; set; }
    public string? LastError { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class SyncCheckpointStatus
{
    public const string Pending    = "Pending";
    public const string InProgress = "InProgress";
    public const string Completed  = "Completed";
    public const string Failed     = "Failed";
}

// The four ERP report APIs that are synchronised month-wise / dealer-wise.
public static class SyncApi
{
    public const string ServiceHistory    = "ServiceHistory";     // ERP DJR  → DMS_ServiceHistory
    public const string VehicleSales      = "VehicleSales";       // ERP VSR  → DMS_VehicleSales
    public const string VehicleDispatches = "VehicleDispatches";  // ERP VDR  → DMS_VehicleDispatches
    public const string LineOrderReport   = "LineOrderReport";    // ERP LOR  → DMS_LineOrderReport

    public static readonly string[] All = { ServiceHistory, VehicleSales, VehicleDispatches, LineOrderReport };

    // Accepts the API name or the short table codes used by the existing admin endpoint (djr, vsr, vdr, lor).
    public static string? Normalize(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "djr" or "servicehistory" or "jobreport" => ServiceHistory,
        "vsr" or "vehiclesales"                  => VehicleSales,
        "vdr" or "vehicledispatches"             => VehicleDispatches,
        "lor" or "lineorderreport"               => LineOrderReport,
        _ => null
    };
}
