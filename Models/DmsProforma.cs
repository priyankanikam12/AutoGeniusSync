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