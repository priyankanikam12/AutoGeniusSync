using Newtonsoft.Json;
namespace AutoGeniusSync.Models;

public class DmsMaterialTransferLabor
{
    public int Id { get; set; }
    public int MaterialTransferItemId { get; set; }

    public long? LbrIdno { get; set; }
    public string? LbrName { get; set; }
    public string? LbrDescription { get; set; }
    public decimal LbrRate { get; set; }
    public decimal SgstPer { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal CgstPer { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal IgstPer { get; set; }
    public decimal IgstAmount { get; set; }

    [JsonIgnore]  // breaks the Labor -> Item -> LabourLines -> ... cycle
    public DmsMaterialTransferItem? MaterialTransferItem { get; set; }
}