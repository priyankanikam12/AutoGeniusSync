namespace AutoGeniusSync.Models;

// One row per nightly current-financial-year refresh (see Services/DataSyncService.FyRefresh.cs).
//   Status: Fetching  – ERP data is being loaded into the *_FyStage tables (live tables untouched)
//           Completed – all four tables were swapped (FY rows replaced) successfully
//           Abandoned – superseded by a newer run (live data was never touched by it)
public class DmsFyRefreshState
{
    public int Id { get; set; }
    public DateOnly RunDate { get; set; }        // IST date the run belongs to
    public DateOnly FyStart { get; set; }        // 1 April of the financial year being refreshed
    public DateOnly ToDate { get; set; }         // yesterday (IST) at the time the run started
    public string Status { get; set; } = "Fetching";
    public string? SwappedTables { get; set; }   // comma list of tables already replaced in this run
    public string? Notes { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
