using Newtonsoft.Json;
namespace AutoGeniusSync.DTOs;
// ─────────────────────────────────────────────
// Vehicle Sales Report (VSR)
// ─────────────────────────────────────────────
public class VsrRequest
{
    [JsonProperty("dealercode")]    public string DealerCode    { get; set; } = "";
    [JsonProperty("vendorid")]      public int    VendorId      { get; set; }
    [JsonProperty("startdate")]     public string StartDate     { get; set; } = "";
    [JsonProperty("enddate")]       public string EndDate       { get; set; } = "";
    [JsonProperty("subvendorcode")] public string SubVendorCode { get; set; } = "";
    [JsonProperty("dealerstatus")]  public string DealerStatus  { get; set; } = "1";
    [JsonProperty("aadharPanReq")]  public string AadharPanReq  { get; set; } = "0";
    [JsonProperty("fameReq")]       public string FameReq       { get; set; } = "2";
}