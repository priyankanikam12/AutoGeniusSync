namespace AutoGeniusSync.Models;

public class DmsProforma
{
    public int Id { get; set; }
    public string? InvoiceNo { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public string? DealerName { get; set; }
    public string? DealerLocation { get; set; }
    public string? ModelName { get; set; }
    public string? ChassisNo { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemDescription { get; set; }
    public string SerialNo { get; set; } = null!;
    public string? RBillNo { get; set; }
    public DateOnly? RBillDate { get; set; }
    public string? PartyName { get; set; }
    public string? PartyState { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}