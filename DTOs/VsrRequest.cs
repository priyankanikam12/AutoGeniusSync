using Newtonsoft.Json;
namespace AutoGeniusSync.DTOs;
public class VsrRequest
{
    [JsonProperty("startdate")]     public string StartDate { get; set; } = "";
    [JsonProperty("enddate")]       public string EndDate { get; set; } = "";
    [JsonProperty("dealercode")]    public string DealerCode { get; set; } = "";
    [JsonProperty("vendorid")]      public int VendorId { get; set; }
    [JsonProperty("subvendorcode")] public string SubVendorCode { get; set; } = "";
    [JsonProperty("DealerStatus")]  public string DealerStatus { get; set; } = "1";
    [JsonProperty("AadharPanReq")] public string AadharPanReq { get; set; } = "0";
    [JsonProperty("FameReq")]       public string FameReq { get; set; } = "2";
}