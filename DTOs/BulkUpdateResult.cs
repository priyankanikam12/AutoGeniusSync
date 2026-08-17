namespace AutoGeniusSync.DTOs;
public class BulkUpdateResult
{
    public int Inserted { get; set; } = 0;
    public int Updated { get; set; }
    public int SkippedNotFound { get; set; }
    public List<string> SkippedKeys { get; set; } = new();
}