namespace AutoGeniusSync.DTOs;
public class SyncResult
{
    public string SyncType { get; set; } = "";
    public DateTime? Date { get; set; }
    public int RecordsFetched { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public string? Error { get; set; }
    public bool Success => Error == null;
}
