using Newtonsoft.Json;
namespace AutoGeniusSync.DTOs;
public class VsrValue
{
    [JsonProperty("Name of the Dealer")] public string? DealerName       { get; set; }
    [JsonProperty("Dealer Code")]         public string? DealerCode       { get; set; }
    [JsonProperty("Invoice No")]          public string? InvoiceNo        { get; set; }
    [JsonProperty("Invoice Date")]        public string? InvoiceDate      { get; set; }
    [JsonProperty("Location")]            public string? Location         { get; set; }
    [JsonProperty("Loc_Code")]            public string? LocCode          { get; set; }
    [JsonProperty("Location City")]       public string? LocationCity     { get; set; }
    [JsonProperty("Cust_DOB")]            public string? CustDOB          { get; set; }
    [JsonProperty("Gender")]              public string? Gender           { get; set; }
    [JsonProperty("Sold To")]             public string? SoldTo           { get; set; }
    [JsonProperty("Account Type")]        public string? AccountType      { get; set; }
    [JsonProperty("Party Email")]         public string? PartyEmail       { get; set; }
    [JsonProperty("Cus_Mob")]             public string? CusMob           { get; set; }
    [JsonProperty("Address1")]            public string? Address1         { get; set; }
    [JsonProperty("Address2")]            public string? Address2         { get; set; }
    [JsonProperty("City")]                public string? City             { get; set; }
    [JsonProperty("State")]               public string? State            { get; set; }
    [JsonProperty("Executive_Name")]      public string? ExecutiveName    { get; set; }
    [JsonProperty("Pin")]                 public string? Pin              { get; set; }
    [JsonProperty("Chasis No")]           public string? ChassisNo        { get; set; }   // FIX: API has this typo (single S)
    [JsonProperty("Motor No")]            public string? MotorNo          { get; set; }
    [JsonProperty("Remarks")]             public string? Remarks          { get; set; }
    [JsonProperty("Item Model")]          public string? ItemModel        { get; set; }
    [JsonProperty("OEM Model")]           public string? OEMModel         { get; set; }
    [JsonProperty("Color Code")]          public string? ColorCode        { get; set; }
    [JsonProperty("Type")]                public string? VehicleType      { get; set; }   // FIX: was "Vehicle Type"
    [JsonProperty("Group")]               public string? VehicleGroup     { get; set; }   // FIX: was "Vehicle Group"
    [JsonProperty("HSNSAC Code")]         public string? HSNSACCode       { get; set; }   // FIX: was "HSN SAC Code"
    [JsonProperty("Sale Type")]           public string? SaleType         { get; set; }
    [JsonProperty("Financed By")]         public string? FinancedBy       { get; set; }
    [JsonProperty("Fin.Amnt")]            public string? FinAmount        { get; set; }   // FIX
    [JsonProperty("Item Rate")]           public string? ItemRate         { get; set; }
    [JsonProperty("Insu. Amnt")]          public string? InsuAmount       { get; set; }   // FIX
    [JsonProperty("Regn. Amnt")]          public string? RegnAmount       { get; set; }   // FIX
    [JsonProperty("Acsry. Amnt")]         public string? AcsryAmount      { get; set; }   // FIX
    [JsonProperty("Pre GST Disc Amnt")]   public string? PreGSTDiscAmount { get; set; }   // FIX
    [JsonProperty("DiscType Name")]       public string? DiscTypeName     { get; set; }   // FIX
    [JsonProperty("Post GST Disc.")]      public string? PostGSTDisc      { get; set; }   // FIX: trailing period
    [JsonProperty("Fame II")]             public string? FameII           { get; set; }
    [JsonProperty("State Fame II")]       public string? StateFameII      { get; set; }
    [JsonProperty("SGST_Per")]            public string? SGSTPer          { get; set; }   // FIX
    [JsonProperty("SGST Amnt")]           public string? SGSTAmount       { get; set; }
    [JsonProperty("CGST_Per")]            public string? CGSTPer          { get; set; }   // FIX
    [JsonProperty("CGST Amnt")]           public string? CGSTAmount       { get; set; }
    [JsonProperty("IGST_Per")]            public string? IGSTPer          { get; set; }   // FIX
    [JsonProperty("IGST Amnt")]           public string? IGSTAmount       { get; set; }
    [JsonProperty("Net Amnt")]            public string? NetAmount        { get; set; }   // FIX
    [JsonProperty("Reference No")]        public string? ReferenceNo      { get; set; }
    [JsonProperty("Booking Date")]        public string? BookingDate      { get; set; }
    [JsonProperty("Total")]               public string? TotalCount       { get; set; }   // FIX: was "Total Count"
    [JsonProperty("Battery")]             public string? Battery          { get; set; }
    [JsonProperty("Battery_Chemical")]    public string? BatteryChemical  { get; set; }   // FIX
    [JsonProperty("Battery_Capacity")]    public string? BatteryCapacity  { get; set; }   // FIX
    [JsonProperty("Battery_Make")]        public string? BatteryMake      { get; set; }   // FIX
    [JsonProperty("Charger_No")]          public string? ChargerNo        { get; set; }   // FIX
    [JsonProperty("Charger_no2")]         public string? ChargerNo2       { get; set; }   // FIX
    [JsonProperty("Converter")]           public string? Converter        { get; set; }
    [JsonProperty("VCU")]                 public string? VCU              { get; set; }
    [JsonProperty("Controller_No")]       public string? ControllerNo     { get; set; }   // FIX
    [JsonProperty("FameII Required")]     public string? FameIIRequired   { get; set; }
    [JsonProperty("Segment Name")]        public string? SegmentName      { get; set; }
    [JsonProperty("Institutional Name")]  public string? InstitutionalName { get; set; }
    [JsonProperty("Scheme Name")]         public string? SchemeName       { get; set; }
}