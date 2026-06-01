using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;
public class VdrValue
{
    [JsonProperty("SaleDate")]         public string? SaleDate { get; set; }
    [JsonProperty("InvoiceNo")]        public string? InvoiceNo { get; set; }
    [JsonProperty("InvoiceDate")]      public string? InvoiceDate { get; set; }
    [JsonProperty("Location")]          public string? Location { get; set; }
    [JsonProperty("LocationCode")]     public string? LocationCode { get; set; }
    [JsonProperty("LocationCity")]     public string? LocationCity { get; set; }
    [JsonProperty("LocationStatus")]   public string? LocationStatus { get; set; }
    [JsonProperty("DealerName")]       public string? DealerName { get; set; }
    [JsonProperty("Zone")]              public string? Zone { get; set; }
    [JsonProperty("AreaOffice")]       public string? AreaOffice { get; set; }
    [JsonProperty("MfgYear")]          public string? MfgYear { get; set; }
    [JsonProperty("BrandName")]        public string? BrandName { get; set; }
    [JsonProperty("ModelCode")]        public string? ModelCode { get; set; }
    [JsonProperty("ColorCode")]        public string? ColorCode { get; set; }
    [JsonProperty("ChassisNo")]         public string? ChassisNo { get; set; }
    [JsonProperty("RegNo")]            public string? RegNo { get; set; }
    [JsonProperty("MotorNo")]          public string? MotorNo { get; set; }
    [JsonProperty("BatteryId")]        public string? BatteryId { get; set; }
    [JsonProperty("BatteryNo")]        public string? BatteryNo { get; set; }
    [JsonProperty("EcuSerialNo")]     public string? EcuSerialNo { get; set; }
    [JsonProperty("EcuImEi")]         public string? EcuImEi { get; set; }
    [JsonProperty("EcuBalMac")]       public string? EcuBalMac { get; set; }
    [JsonProperty("ImmoblizerNo")]     public string? ImmoblizerNo { get; set; }
    [JsonProperty("BikeSimId")]       public string? BikeSimId { get; set; }
    [JsonProperty("BikeMobileNo")]    public string? BikeMobileNo { get; set; }
    [JsonProperty("ChargerNo")]        public string? ChargerNo { get; set; }
    [JsonProperty("ControllerNo")]     public string? ControllerNo { get; set; }
    [JsonProperty("SoundbarSerialNo")] public string? SoundbarSerialNo { get; set; }
    [JsonProperty("SoundbarBalMac")]  public string? SoundbarBalMac { get; set; }
    [JsonProperty("Voltage")]           public string? Voltage { get; set; }
    [JsonProperty("RegNumber")]        public string? RegNumber { get; set; }
    [JsonProperty("StartDate")]        public string? StartDate { get; set; }
    [JsonProperty("Tyre1")]            public string? Tyre1 { get; set; }
    [JsonProperty("Tyre2")]            public string? Tyre2 { get; set; }
    [JsonProperty("VehicleStatus")]    public string? VehicleStatus { get; set; }
    [JsonProperty("BookingId")]        public string? BookingId { get; set; }
    [JsonProperty("BillNo")]           public string? BillNo { get; set; }
    [JsonProperty("BillDate")]         public string? BillDate { get; set; }
    [JsonProperty("BillType")]         public string? BillType { get; set; }
    [JsonProperty("FinancerName")]     public string? FinancerName { get; set; }
    [JsonProperty("FinAmount")]        public string? FinAmount { get; set; }
    [JsonProperty("NameOfParty")] public string? NameOfParty { get; set; }
    [JsonProperty("Address1")]          public string? Address1 { get; set; }
    [JsonProperty("Address2")]          public string? Address2 { get; set; }
    [JsonProperty("State")]             public string? State { get; set; }
    [JsonProperty("City")]              public string? City { get; set; }
    [JsonProperty("Pin")]               public string? Pin { get; set; }
    [JsonProperty("MobileNo")]         public string? MobileNo { get; set; }
    [JsonProperty("Email")]             public string? Email { get; set; }
    [JsonProperty("AppPush")]          public string? AppPush { get; set; }
    [JsonProperty("LeadId")]            public string? LeadId { get; set; }
    [JsonProperty("VCU")]               public string? Vcu { get; set; }
}