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

    // ── Mirrored from DmsProforma ──
    public string? InsuranceType { get; set; }      // 'Paid' / 'Unpaid'
    public string? InsuranceDetails { get; set; }
    public string? JobCardNo { get; set; }
    public DateOnly? JobCardDate { get; set; }
    public decimal? Cgst { get; set; }
    public decimal? Sgst { get; set; }
    public decimal? Igst { get; set; }
    public decimal? TotalAmount { get; set; }

    public decimal? ItemRate { get; set; }
    public decimal? ItemQty { get; set; }
    public decimal? Mrp { get; set; }
    public string? DiscountType { get; set; }        // '%' / 'Value'
    public decimal? DiscountValue { get; set; }
    public decimal? DiscountPercent { get; set; }
    public string? PartNo { get; set; }
    public string? PartName { get; set; }
    public string? PartDescription { get; set; }
    public decimal? Labour { get; set; }
    public string? LabourDescription { get; set; }
    public string? MaterialCode { get; set; }
    public DateOnly? MaterialDate { get; set; }
    public string? DealerType { get; set; }
    public string? UniqueKey { get; set; }
}