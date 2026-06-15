// DTOs/LorRequest.cs
using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

public class LorRequest
{
    [JsonProperty("vendorid")]   public int    VendorId   { get; set; }
    [JsonProperty("startdate")]  public string StartDate  { get; set; } = "";
    [JsonProperty("enddate")]    public string EndDate    { get; set; } = "";
    [JsonProperty("dealercode")] public string DealerCode { get; set; } = "";
}