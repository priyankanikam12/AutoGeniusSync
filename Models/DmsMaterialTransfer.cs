namespace AutoGeniusSync.Models;

public class DmsMaterialTransfer
{
    public int Id { get; set; }

    public string? DealerName { get; set; }
    public string? DealerCode { get; set; }
    public long? SourceUniqueId { get; set; }   // source "uniqueId"
    public long? SourceJobId { get; set; }      // source "uniqueKey", labeled Job Id
    public int? DocNo { get; set; }
    public DateTime? DocDate { get; set; }
    public string? DocType { get; set; }
    public string? Location { get; set; }
    public string? LocCode { get; set; }
    public string? TechnicianName { get; set; }

    // our own dedup key: DealerCode + DocNo + DocType + SourceJobId
    public string? UniqueKey { get; set; }
    public string? Action { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<DmsMaterialTransferItem> Items { get; set; } = new();
}