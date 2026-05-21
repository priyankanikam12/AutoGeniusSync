using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

// ─────────────────────────────────────────────
// Generic API wrapper
// ─────────────────────────────────────────────
public class ErpApiResponse<T>
{
    [JsonProperty("Valid")] public bool Valid { get; set; }
    [JsonProperty("Description")] public string? Description { get; set; }
    [JsonProperty("Value")] public List<T>? Value { get; set; }
}

// ─────────────────────────────────────────────
// Login
// ─────────────────────────────────────────────
public class LoginRequest
{
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("password")] public string Password { get; set; } = "";
}

public class LoginValue
{
    [JsonProperty("accesstoken")]       public string? AccessToken { get; set; }
    [JsonProperty("LoginEmail_Idno")]   public string? LoginEmailIdno { get; set; }
    [JsonProperty("loginemail")]        public string? LoginEmail { get; set; }
    [JsonProperty("vendorname")]        public string? VendorName { get; set; }
    [JsonProperty("vendorcode")]        public string? VendorCode { get; set; }
    [JsonProperty("vendorid")]          public string? VendorId { get; set; }
}

// ─────────────────────────────────────────────
// Pincode / Dealer
// ─────────────────────────────────────────────
public class DealerValue
{
    [JsonProperty("DealerCode")]            public string? DealerCode { get; set; }
    [JsonProperty("DealerCompany")]         public string? DealerCompany { get; set; }
    [JsonProperty("ContactNo")]             public string? ContactNo { get; set; }
    [JsonProperty("AlternateContactNo")]    public string? AlternateContactNo { get; set; }
    [JsonProperty("DealerStateName")]       public string? DealerStateName { get; set; }
    [JsonProperty("DealerCityName")]        public string? DealerCityName { get; set; }
    [JsonProperty("PinCode")]               public string? PinCode { get; set; }
    [JsonProperty("ActiveStatus")]          public string? ActiveStatus { get; set; }
}

// ─────────────────────────────────────────────
// Daily Job Report
// ─────────────────────────────────────────────
public class DjrRequest
{
    [JsonProperty("vendorid")]      public int VendorId { get; set; }
    [JsonProperty("startdate")]     public string StartDate { get; set; } = "";
    [JsonProperty("enddate")]       public string EndDate { get; set; } = "";
    [JsonProperty("dealercode")]    public string DealerCode { get; set; } = "";
}

public class DjrValue
{
    [JsonProperty("Comp Name")]                 public string? CompName { get; set; }
    [JsonProperty("Dealer Code")]               public string? DealerCode { get; set; }
    [JsonProperty("Job Date")]                  public string? JobDate { get; set; }
    [JsonProperty("Job No")]                    public string? JobNo { get; set; }
    [JsonProperty("In Time")]                   public string? InTime { get; set; }
    [JsonProperty("Job Category")]              public string? JobCategory { get; set; }
    [JsonProperty("FFR Per%")]                  public string? FFRPercentage { get; set; }
    [JsonProperty("Doc No")]                    public string? DocNo { get; set; }
    [JsonProperty("Doc Type")]                  public string? DocType { get; set; }
    [JsonProperty("Doc Date")]                  public string? DocDate { get; set; }
    [JsonProperty("Close Time")]                public string? CloseTime { get; set; }
    [JsonProperty("Location")]                  public string? Location { get; set; }
    [JsonProperty("Model")]                     public string? Model { get; set; }
    [JsonProperty("Brand Name")]                public string? BrandName { get; set; }
    [JsonProperty("Party Name")]                public string? PartyName { get; set; }
    [JsonProperty("Mobile Number")]             public string? MobileNumber { get; set; }
    [JsonProperty("Supervisor")]                public string? Supervisor { get; set; }
    [JsonProperty("Technician")]                public string? Technician { get; set; }
    [JsonProperty("KMS")]                       public string? KMS { get; set; }
    [JsonProperty("Service Head")]              public string? ServiceHead { get; set; }
    [JsonProperty("Job Type")]                  public string? JobType { get; set; }
    [JsonProperty("Reg No")]                    public string? RegNo { get; set; }
    [JsonProperty("Vehicle Type")]              public string? VehicleType { get; set; }
    [JsonProperty("Engine No")]                 public string? EngineNo { get; set; }
    [JsonProperty("Chassis No")]                public string? ChassisNo { get; set; }
    [JsonProperty("Battery SerialNo1")]         public string? BatterySerialNo1 { get; set; }
    [JsonProperty("Battery SerialNo2")]         public string? BatterySerialNo2 { get; set; }
    [JsonProperty("Battery SerialNo3")]         public string? BatterySerialNo3 { get; set; }
    [JsonProperty("Battery SerialNo4")]         public string? BatterySerialNo4 { get; set; }
    [JsonProperty("Battery SerialNo5")]         public string? BatterySerialNo5 { get; set; }
    [JsonProperty("Battery SerialNo6")]         public string? BatterySerialNo6 { get; set; }
    [JsonProperty("IndividualAH Battery1")]     public string? IndividualAHBattery1 { get; set; }
    [JsonProperty("IndividualAH Battery2")]     public string? IndividualAHBattery2 { get; set; }
    [JsonProperty("IndividualAH Battery3")]     public string? IndividualAHBattery3 { get; set; }
    [JsonProperty("IndividualAH Battery4")]     public string? IndividualAHBattery4 { get; set; }
    [JsonProperty("IndividualAH Battery5")]     public string? IndividualAHBattery5 { get; set; }
    [JsonProperty("IndividualAH Battery6")]     public string? IndividualAHBattery6 { get; set; }
    [JsonProperty("Sale Date")]                 public string? SaleDate { get; set; }
    [JsonProperty("Coupon No")]                 public string? CouponNo { get; set; }
    [JsonProperty("Expected delivery date")]    public string? ExpectedDeliveryDate { get; set; }
    [JsonProperty("Proforma Date")]             public string? ProformaDate { get; set; }
    [JsonProperty("Invoice Date")]              public string? InvoiceDate { get; set; }
    [JsonProperty("Estimated Job expenses")]    public string? EstimatedJobExpenses { get; set; }
    [JsonProperty("Labour hours")]              public string? LabourHours { get; set; }
    [JsonProperty("Parts")]                     public string? Parts { get; set; }
    [JsonProperty("Accessory")]                 public string? Accessory { get; set; }
    [JsonProperty("Oil")]                       public string? Oil { get; set; }
    [JsonProperty("Labour")]                    public string? Labour { get; set; }
    [JsonProperty("Outside Work")]              public string? OutsideWork { get; set; }
    [JsonProperty("Total W/O Tax")]             public string? TotalWOTax { get; set; }
    [JsonProperty("GST_Amnt")]                  public string? GSTAmount { get; set; }
    [JsonProperty("IGST_Amnt")]                 public string? IGSTAmount { get; set; }
    [JsonProperty("Net Total")]                 public string? NetTotal { get; set; }
}
