using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;
public class CallCentreDealerValue
{
    [JsonProperty("DealerCode")]            public string? DealerCode { get; set; }
    [JsonProperty("DealerCompany")]         public string? DealerCompany { get; set; }
    [JsonProperty("ContactNo")]             public string? ContactNo { get; set; }
    [JsonProperty("AlternateContactNo")]    public string? AlternateContactNo { get; set; }
    [JsonProperty("DealerStateName")]       public string? DealerStateName { get; set; }
    [JsonProperty("PinCode")]               public string? PinCode { get; set; }
    [JsonProperty("ActiveStatus")]          public string? ActiveStatus { get; set; }
}