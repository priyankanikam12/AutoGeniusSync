namespace AutoGeniusSync.DTOs;

public class BulkInsertResult
{
    public int Inserted { get; set; }
    public int SkippedDuplicates { get; set; }
    public List<string> SkippedKeys { get; set; } = new();
}