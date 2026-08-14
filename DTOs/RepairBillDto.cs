using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Repair Bill — POST/PUT request body
// Unique key: (Location, BillNo)
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
    [JsonProperty("Date_Added")]    public string? DateAdded { get; set; }      // M/d/yyyy h:mm:ss tt
    [JsonProperty("Date_Modified")] public string? DateModified { get; set; }
    [JsonProperty("Chassis No")]    public string? ChassisNo { get; set; }
}