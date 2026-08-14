using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Proforma — POST/PUT request body
// Unique key: SerialNo
// ─────────────────────────────────────────────
public class ProformaDto
{
    [JsonProperty("Invoice No")]      public string? InvoiceNo { get; set; }
    [JsonProperty("Invoice Date")]    public string? InvoiceDate { get; set; }   // dd-MM-yyyy
    [JsonProperty("Dealer Name")]     public string? DealerName { get; set; }
    [JsonProperty("Dealer Location")] public string? DealerLocation { get; set; }
    [JsonProperty("Model Name")]      public string? ModelName { get; set; }
    [JsonProperty("Chasis No")]       public string? ChassisNo { get; set; }     // matches ERP's typo pattern
    [JsonProperty("Item Code")]       public string? ItemCode { get; set; }
    [JsonProperty("Item Description")] public string? ItemDescription { get; set; }
    [JsonProperty("Serial No")]       public string SerialNo { get; set; } = "";
    [JsonProperty("RBill No")]        public string? RBillNo { get; set; }
    [JsonProperty("RBill Date")]      public string? RBillDate { get; set; }
    [JsonProperty("Party Name")]      public string? PartyName { get; set; }
    [JsonProperty("Party State")]     public string? PartyState { get; set; }
}