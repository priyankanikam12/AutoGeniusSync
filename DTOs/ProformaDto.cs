using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Proforma — POST/PUT request body
// Unique key: SerialNo (or UniqueKey, if supplied)
// ─────────────────────────────────────────────
public class ProformaDto
{
    [JsonProperty("Invoice No")]      public string? InvoiceNo { get; set; }
    [JsonProperty("Invoice Date")]    public string? InvoiceDate { get; set; }   // dd-MM-yyyy
    [JsonProperty("Dealer Name")]     public string? DealerName { get; set; }
    [JsonProperty("Dealer Location")] public string? DealerLocation { get; set; }
    [JsonProperty("Model Name")]      public string? ModelName { get; set; }
    [JsonProperty("Chasis No")]       public string? ChassisNo { get; set; }
    [JsonProperty("Item Code")]       public string? ItemCode { get; set; }
    [JsonProperty("Item Description")] public string? ItemDescription { get; set; }
    [JsonProperty("Serial No")]       public string SerialNo { get; set; } = "";
    [JsonProperty("RBill No")]        public string? RBillNo { get; set; }
    [JsonProperty("RBill Date")]      public string? RBillDate { get; set; }
    [JsonProperty("Party Name")]      public string? PartyName { get; set; }
    [JsonProperty("Party State")]     public string? PartyState { get; set; }

    [JsonProperty("Insurance Type")]    public string? InsuranceType { get; set; }
    [JsonProperty("Insurance Details")] public string? InsuranceDetails { get; set; }
    [JsonProperty("Job Card No")]       public string? JobCardNo { get; set; }
    [JsonProperty("Job Card Date")]     public string? JobCardDate { get; set; }   // dd-MM-yyyy
    [JsonProperty("CGST")]              public string? Cgst { get; set; }
    [JsonProperty("SGST")]              public string? Sgst { get; set; }
    [JsonProperty("IGST")]              public string? Igst { get; set; }
    [JsonProperty("Total Amount")]      public string? TotalAmount { get; set; }

    [JsonProperty("Item Rate")]         public string? ItemRate { get; set; }
    [JsonProperty("Item Qty")]          public string? ItemQty { get; set; }
    [JsonProperty("MRP")]               public string? Mrp { get; set; }
    [JsonProperty("Amount")]            public string? Amount { get; set; }
    [JsonProperty("Discount Type")]     public string? DiscountType { get; set; }  // '%' / 'Value'
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