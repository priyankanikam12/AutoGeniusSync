using Newtonsoft.Json;
namespace AutoGeniusSync.DTOs;
public class VsrValue
{
    [JsonProperty("Dealer Name")]         public string? DealerName       { get; set; }
    [JsonProperty("Dealer Code")]         public string? DealerCode       { get; set; }
    [JsonProperty("Invoice No")]          public string? InvoiceNo        { get; set; }
    [JsonProperty("Invoice Date")]        public string? InvoiceDate      { get; set; }
    [JsonProperty("Location")]            public string? Location         { get; set; }
    [JsonProperty("Loc Code")]            public string? LocCode          { get; set; }
    [JsonProperty("Location City")]       public string? LocationCity     { get; set; }
    [JsonProperty("Cust DOB")]            public string? CustDOB          { get; set; }
    [JsonProperty("Gender")]              public string? Gender           { get; set; }
    [JsonProperty("Sold To")]             public string? SoldTo           { get; set; }
    [JsonProperty("Account Type")]        public string? AccountType      { get; set; }
    [JsonProperty("Party Email")]         public string? PartyEmail       { get; set; }
    [JsonProperty("Cus Mob")]             public string? CusMob           { get; set; }
    [JsonProperty("Address1")]            public string? Address1         { get; set; }
    [JsonProperty("Address2")]            public string? Address2         { get; set; }
    [JsonProperty("City")]                public string? City             { get; set; }
    [JsonProperty("State")]               public string? State            { get; set; }
    [JsonProperty("Executive Name")]      public string? ExecutiveName    { get; set; }
    [JsonProperty("Pin")]                 public string? Pin              { get; set; }
    [JsonProperty("Chassis No")]          public string? ChassisNo        { get; set; }
    [JsonProperty("Motor No")]            public string? MotorNo          { get; set; }
    [JsonProperty("Remarks")]             public string? Remarks          { get; set; }
    [JsonProperty("Item Model")]          public string? ItemModel        { get; set; }
    [JsonProperty("OEM Model")]           public string? OEMModel         { get; set; }
    [JsonProperty("Color Code")]          public string? ColorCode        { get; set; }
    [JsonProperty("Vehicle Type")]        public string? VehicleType      { get; set; }
    [JsonProperty("Vehicle Group")]       public string? VehicleGroup     { get; set; }
    [JsonProperty("HSN SAC Code")]        public string? HSNSACCode       { get; set; }
    [JsonProperty("Sale Type")]           public string? SaleType         { get; set; }
    [JsonProperty("Financed By")]         public string? FinancedBy       { get; set; }
    [JsonProperty("Fin Amount")]          public string? FinAmount        { get; set; }
    [JsonProperty("Item Rate")]           public string? ItemRate         { get; set; }
    [JsonProperty("Insu Amount")]         public string? InsuAmount       { get; set; }
    [JsonProperty("Regn Amount")]         public string? RegnAmount       { get; set; }
    [JsonProperty("Acsry Amount")]        public string? AcsryAmount      { get; set; }
    [JsonProperty("Pre GST Disc Amount")] public string? PreGSTDiscAmount { get; set; }
    [JsonProperty("Disc Type Name")]      public string? DiscTypeName     { get; set; }
    [JsonProperty("Post GST Disc")]       public string? PostGSTDisc      { get; set; }
    [JsonProperty("Fame II")]             public string? FameII           { get; set; }
    [JsonProperty("State Fame II")]       public string? StateFameII      { get; set; }
    [JsonProperty("SGST Per")]            public string? SGSTPer          { get; set; }
    [JsonProperty("SGST Amount")]         public string? SGSTAmount       { get; set; }
    [JsonProperty("CGST Per")]            public string? CGSTPer          { get; set; }
    [JsonProperty("CGST Amount")]         public string? CGSTAmount       { get; set; }
    [JsonProperty("IGST Per")]            public string? IGSTPer          { get; set; }
    [JsonProperty("IGST Amount")]         public string? IGSTAmount       { get; set; }
    [JsonProperty("Net Amount")]          public string? NetAmount        { get; set; }
    [JsonProperty("Reference No")]        public string? ReferenceNo      { get; set; }
    [JsonProperty("Booking Date")]        public string? BookingDate      { get; set; }
    [JsonProperty("Total Count")]         public string? TotalCount       { get; set; }
    [JsonProperty("Battery")]             public string? Battery          { get; set; }
    [JsonProperty("Battery Chemical")]    public string? BatteryChemical  { get; set; }
    [JsonProperty("Battery Capacity")]    public string? BatteryCapacity  { get; set; }
    [JsonProperty("Battery Make")]        public string? BatteryMake      { get; set; }
    [JsonProperty("Charger No")]          public string? ChargerNo        { get; set; }
    [JsonProperty("Charger No2")]         public string? ChargerNo2       { get; set; }
    [JsonProperty("Converter")]           public string? Converter        { get; set; }
    [JsonProperty("VCU")]                 public string? VCU              { get; set; }
    [JsonProperty("Controller No")]       public string? ControllerNo     { get; set; }
    [JsonProperty("Fame II Required")]    public string? FameIIRequired   { get; set; }
    [JsonProperty("Segment Name")]        public string? SegmentName      { get; set; }
    [JsonProperty("Institutional Name")]  public string? InstitutionalName { get; set; }
    [JsonProperty("Scheme Name")]         public string? SchemeName       { get; set; }
}