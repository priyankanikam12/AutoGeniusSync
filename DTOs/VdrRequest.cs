using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Vehicle Dispatch Report (VDR)
// ─────────────────────────────────────────────
public class VdrRequest
{
    [JsonProperty("vendorid")]      public int    VendorId      { get; set; }
    [JsonProperty("fromdate")]      public string FromDate      { get; set; } = "";
    [JsonProperty("todate")]        public string ToDate        { get; set; } = "";
    [JsonProperty("dealercode")]    public string DealerCode    { get; set; } = "";
    [JsonProperty("locationcode")]  public string LocationCode  { get; set; } = "";
    [JsonProperty("chassisno")]     public string ChassisNo     { get; set; } = "";
    [JsonProperty("mobileno")]      public string MobileNo      { get; set; } = "";
    [JsonProperty("vhclstatus")]    public string VhclStatus    { get; set; } = "ALL";
    [JsonProperty("subvendorcode")] public string SubVendorCode { get; set; } = "";
}