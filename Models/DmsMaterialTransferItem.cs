using Newtonsoft.Json;

namespace AutoGeniusSync.Models;

public class DmsMaterialTransferItem
{
    public int Id { get; set; }
    public int MaterialTransferId { get; set; }

    public long? SourceLineId { get; set; }
    public long? ItemIdno { get; set; }
    public string? ItemName { get; set; }
    public string? ItemDescription { get; set; }
    public string? ItemType { get; set; }
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
    public decimal SgstPer { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal CgstPer { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal IgstPer { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal Discount { get; set; }
    public decimal Mrp { get; set; }

    [JsonIgnore]  // breaks the Item -> MaterialTransfer -> Items -> ... cycle
    public DmsMaterialTransfer? MaterialTransfer { get; set; }

    public List<DmsMaterialTransferLabor> LabourLines { get; set; } = new();
}

