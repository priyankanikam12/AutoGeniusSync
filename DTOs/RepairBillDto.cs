using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Repair Bill — POST/PUT request body
// Unique key: (Location, BillNo) (or UniqueKey, if supplied)
// ─────────────────────────────────────────────
public class RepairBillDto
{
    [JsonProperty("Date")]          public string? Date { get; set; }          // dd-MM-yyyy
    [JsonProperty("Bill No.")]      public string BillNo { get; set; } = "";
    [JsonProperty("Location")]      public string Location { get; set; } = "";
    [JsonProperty("Party Name")]    public string? PartyName { get; set; }
    [JsonProperty("Reg No")]        public string? RegNo { get; set; }
    [JsonProperty("BillType")]      public string? BillType { get; set; }
    [JsonProperty("Job No.")]       public string? JobNo { get; set; }
    [JsonProperty("Net Amnt")]      public string? NetAmount { get; set; }
    [JsonProperty("UserName")]      public string? UserName { get; set; }
    [JsonProperty("UserNameEdit")]  public string? UserNameEdit { get; set; }
    [JsonProperty("Date_Added")]    public string? DateAdded { get; set; }
    [JsonProperty("Date_Modified")] public string? DateModified { get; set; }
    [JsonProperty("Chassis No")]    public string? ChassisNo { get; set; }

    [JsonProperty("Insurance Type")]    public string? InsuranceType { get; set; }
    [JsonProperty("Insurance Details")] public string? InsuranceDetails { get; set; }
    [JsonProperty("Job Card No")]       public string? JobCardNo { get; set; }
    [JsonProperty("Job Card Date")]     public string? JobCardDate { get; set; }
    [JsonProperty("CGST")]              public string? Cgst { get; set; }
    [JsonProperty("SGST")]              public string? Sgst { get; set; }
    [JsonProperty("IGST")]              public string? Igst { get; set; }
    [JsonProperty("Total Amount")]      public string? TotalAmount { get; set; }

    [JsonProperty("Item Rate")]         public string? ItemRate { get; set; }
    [JsonProperty("Item Qty")]          public string? ItemQty { get; set; }
    [JsonProperty("MRP")]               public string? Mrp { get; set; }
    [JsonProperty("Amount")]            public string? Amount { get; set; }
    [JsonProperty("Discount Type")]     public string? DiscountType { get; set; }
    [JsonProperty("Discount Value")]    public string? DiscountValue { get; set; }
    [JsonProperty("Discount Percent")]  public string? DiscountPercent { get; set; }
    [JsonProperty("Part No")]           public string? PartNo { get; set; }
    [JsonProperty("Part Name")]         public string? PartName { get; set; }
    [JsonProperty("Part Description")]  public string? PartDescription { get; set; }
    [JsonProperty("Labour")]            public string? Labour { get; set; }
    [JsonProperty("Labour Description")] public string? LabourDescription { get; set; }
    [JsonProperty("Material Code")] public string? MaterialCode { get; set; }
    [JsonProperty("Material Date")] public string? MaterialDate { get; set; }   // dd-MM-yyyy
    [JsonProperty("Dealer Type")]    public string? DealerType { get; set; }
    [JsonProperty("Unique Key")]        public string? UniqueKey { get; set; }
}