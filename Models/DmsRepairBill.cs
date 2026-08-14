namespace AutoGeniusSync.Models;

public class DmsRepairBill
{
    public int Id { get; set; }
    public DateOnly? BillDate { get; set; }
    public string BillNo { get; set; } = null!;
    public string Location { get; set; } = null!;
    public string? PartyName { get; set; }
    public string? RegNo { get; set; }
    public string? BillType { get; set; }
    public string? JobNo { get; set; }
    public decimal? NetAmount { get; set; }
    public string? UserName { get; set; }
    public string? UserNameEdit { get; set; }
    public DateTime? DateAdded { get; set; }
    public DateTime? DateModified { get; set; }
    public string? ChassisNo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}