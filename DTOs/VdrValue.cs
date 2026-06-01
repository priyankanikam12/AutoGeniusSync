using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;
public class VdrValue
{
    [JsonProperty("Sale Date")]         public string? SaleDate { get; set; }
    [JsonProperty("Invoice No")]        public string? InvoiceNo { get; set; }
    [JsonProperty("Invoice Date")]      public string? InvoiceDate { get; set; }
    [JsonProperty("Location")]          public string? Location { get; set; }
    [JsonProperty("Location Code")]     public string? LocationCode { get; set; }
    [JsonProperty("Location City")]     public string? LocationCity { get; set; }
    [JsonProperty("Location Status")]   public string? LocationStatus { get; set; }
    [JsonProperty("Dealer Name")]       public string? DealerName { get; set; }
    [JsonProperty("Zone")]              public string? Zone { get; set; }
    [JsonProperty("Area Office")]       public string? AreaOffice { get; set; }
    [JsonProperty("Mfg Year")]          public string? MfgYear { get; set; }
    [JsonProperty("Brand Name")]        public string? BrandName { get; set; }
    [JsonProperty("Model Code")]        public string? ModelCode { get; set; }
    [JsonProperty("Color Code")]        public string? ColorCode { get; set; }
    [JsonProperty("Chasis No")]         public string? ChassisNo { get; set; }
    [JsonProperty("Reg No")]            public string? RegNo { get; set; }
    [JsonProperty("Motor No")]          public string? MotorNo { get; set; }
    [JsonProperty("Battery ID")]        public string? BatteryId { get; set; }
    [JsonProperty("Battery No")]        public string? BatteryNo { get; set; }
    [JsonProperty("ECU Serial No")]     public string? EcuSerialNo { get; set; }
    [JsonProperty("ECU Im EI")]         public string? EcuImEi { get; set; }
    [JsonProperty("ECU BAL MAC")]       public string? EcuBalMac { get; set; }
    [JsonProperty("Immoblizer No")]     public string? ImmoblizerNo { get; set; }
    [JsonProperty("Bike Sim Id")]       public string? BikeSimId { get; set; }
    [JsonProperty("Bike Mobile No")]    public string? BikeMobileNo { get; set; }
    [JsonProperty("Charger No")]        public string? ChargerNo { get; set; }
    [JsonProperty("Controller No")]     public string? ControllerNo { get; set; }
    [JsonProperty("Soundbar Serial No")] public string? SoundbarSerialNo { get; set; }
    [JsonProperty("Soundbar BAL MAC")]  public string? SoundbarBalMac { get; set; }
    [JsonProperty("Voltage")]           public string? Voltage { get; set; }
    [JsonProperty("Reg Number")]        public string? RegNumber { get; set; }
    [JsonProperty("Start Date")]        public string? StartDate { get; set; }
    [JsonProperty("Tyre 1")]            public string? Tyre1 { get; set; }
    [JsonProperty("Tyre 2")]            public string? Tyre2 { get; set; }
    [JsonProperty("Vehicle Status")]    public string? VehicleStatus { get; set; }
    [JsonProperty("Booking Id")]        public string? BookingId { get; set; }
    [JsonProperty("Bill No")]           public string? BillNo { get; set; }
    [JsonProperty("Bill Date")]         public string? BillDate { get; set; }
    [JsonProperty("Bill Type")]         public string? BillType { get; set; }
    [JsonProperty("Financer Name")]     public string? FinancerName { get; set; }
    [JsonProperty("Fin Amount")]        public string? FinAmount { get; set; }
    [JsonProperty("Name of the Party")] public string? NameOfParty { get; set; }
    [JsonProperty("Address1")]          public string? Address1 { get; set; }
    [JsonProperty("Address2")]          public string? Address2 { get; set; }
    [JsonProperty("State")]             public string? State { get; set; }
    [JsonProperty("City")]              public string? City { get; set; }
    [JsonProperty("Pin")]               public string? Pin { get; set; }
    [JsonProperty("Mobile No")]         public string? MobileNo { get; set; }
    [JsonProperty("Email")]             public string? Email { get; set; }
    [JsonProperty("App Push")]          public string? AppPush { get; set; }
    [JsonProperty("LeadId")]            public string? LeadId { get; set; }
    [JsonProperty("VCU")]               public string? Vcu { get; set; }
}