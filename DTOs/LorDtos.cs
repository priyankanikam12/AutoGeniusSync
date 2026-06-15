// using Newtonsoft.Json;

// namespace AutoGeniusSync.DTOs;

// public class LorRequest
// {
//     [JsonProperty("vendorid")]   public int    VendorId   { get; set; }
//     [JsonProperty("startdate")]  public string StartDate  { get; set; } = "";
//     [JsonProperty("enddate")]    public string EndDate    { get; set; } = "";
//     [JsonProperty("dealercode")] public string DealerCode { get; set; } = "";
// }

// public class LorValue
// {
//     [JsonProperty("Dealer Name")]      public string? DealerName    { get; set; }
//     [JsonProperty("Dealer Code")]      public string? DealerCode    { get; set; }  // ← space
//     [JsonProperty("UniqueId")]         public string? UniqueId      { get; set; }
//     [JsonProperty("Loc Code")]         public string? LocCode       { get; set; }
//     [JsonProperty("Doc Date")]         public string? DocDate       { get; set; }
//     [JsonProperty("Doc No")]           public string? DocNo         { get; set; }
//     [JsonProperty("Doc Type")]         public string? DocType       { get; set; }
//     [JsonProperty("Job Date")]         public string? JobDate       { get; set; }
//     [JsonProperty("Job No")]           public string? JobNo         { get; set; }
//     [JsonProperty("Brand Name")]       public string? BrandName     { get; set; }
//     [JsonProperty("Model")]            public string? Model         { get; set; }
//     [JsonProperty("JOBCard Type")]     public string? JobCardType   { get; set; }
//     [JsonProperty("Payment Mode")]     public string? PaymentMode   { get; set; }
//     [JsonProperty("Party Name")]       public string? PartyName     { get; set; }
//     [JsonProperty("Prty_Mobile")]      public string? PartyMobile   { get; set; }
//     [JsonProperty("Reg. No.")]         public string? RegNo         { get; set; }
//     [JsonProperty("Vehicle Type")]     public string? VehicleType   { get; set; }
//     [JsonProperty("Chassis No")]       public string? ChassisNo     { get; set; }
//     [JsonProperty("Location")]         public string? Location      { get; set; }
//     [JsonProperty("Item Name")]        public string? ItemName      { get; set; }
//     [JsonProperty("Item Description")] public string? ItemDescription { get; set; }
//     [JsonProperty("Item Type")]        public string? ItemType      { get; set; }
//     [JsonProperty("Qty")]              public string? Qty           { get; set; }
//     [JsonProperty("Rate")]             public string? Rate          { get; set; }
//     [JsonProperty("Total")]            public string? Total         { get; set; }

//     // Tax — API sends BOTH "SGST%" and "SGST_Per"; map both to catch whichever arrives
//     [JsonProperty("SGST%")]            public string? SgstPer       { get; set; }
//     [JsonProperty("SGST Amnt")]        public string? SgstAmount    { get; set; }
//     [JsonProperty("CGST%")]            public string? CgstPer       { get; set; }
//     [JsonProperty("CGST Amnt")]        public string? CgstAmount    { get; set; }
//     [JsonProperty("IGST%")]            public string? IgstPer       { get; set; }
//     [JsonProperty("IGST Amnt")]        public string? IgstAmount    { get; set; }
//     [JsonProperty("Discount")]         public string? Discount      { get; set; }
//     [JsonProperty("Total Amount")]     public string? TotalAmount   { get; set; }
//     [JsonProperty("MRP")]              public string? Mrp           { get; set; }
//     [JsonProperty("Dealer Type")]      public string? DealerType    { get; set; }

//     // Duplicate tax fields the API also sends — ignore but map to avoid parse warnings
//     [JsonProperty("SGST_Per")]         public string? SgstPerAlt    { get; set; }
//     [JsonProperty("CGST_Per")]         public string? CgstPerAlt    { get; set; }
//     [JsonProperty("IGST_Per")]         public string? IgstPerAlt    { get; set; }
// }