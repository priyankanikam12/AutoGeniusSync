// DTOs/MaterialTransferRecordDto.cs
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs;

public class MaterialTransferRecordDto
{
    [JsonPropertyName("dealerName")]  [JsonProperty("dealerName")]  public string? DealerName { get; set; }
    [JsonPropertyName("dealerCode")]  [JsonProperty("dealerCode")]  public string? DealerCode { get; set; }
    [JsonPropertyName("uniqueId")]    [JsonProperty("uniqueId")]    public long? UniqueId { get; set; }
    [JsonPropertyName("uniqueKey")]   [JsonProperty("uniqueKey")]   public long? JobId { get; set; }
    [JsonPropertyName("docNo")]       [JsonProperty("docNo")]       public int? DocNo { get; set; }
    [JsonPropertyName("docDate")]     [JsonProperty("docDate")]     public DateTime? DocDate { get; set; }
    [JsonPropertyName("docType")]     [JsonProperty("docType")]     public string? DocType { get; set; }
    [JsonPropertyName("location")]    [JsonProperty("location")]    public string? Location { get; set; }
    [JsonPropertyName("locCode")]     [JsonProperty("locCode")]     public string? LocCode { get; set; }
    [JsonPropertyName("technicianName")] [JsonProperty("technicianName")] public string? TechnicianName { get; set; }

    [JsonPropertyName("TranDetl")] [JsonProperty("TranDetl")]
    public List<MaterialTransferItemDto> TranDetl { get; set; } = new();
}

public class MaterialTransferItemDto
{
    [JsonPropertyName("Id")]              [JsonProperty("Id")]              public long? Id { get; set; }
    [JsonPropertyName("Item_Idno")]        [JsonProperty("Item_Idno")]        public long? ItemIdno { get; set; }
    [JsonPropertyName("itemName")]         [JsonProperty("itemName")]         public string? ItemName { get; set; }
    [JsonPropertyName("itemDescription")]  [JsonProperty("itemDescription")]  public string? ItemDescription { get; set; }
    [JsonPropertyName("itemType")]         [JsonProperty("itemType")]         public string? ItemType { get; set; }
    [JsonPropertyName("qty")]              [JsonProperty("qty")]              public decimal? Qty { get; set; }
    [JsonPropertyName("rate")]             [JsonProperty("rate")]             public decimal? Rate { get; set; }
    [JsonPropertyName("sgstPer")]          [JsonProperty("sgstPer")]          public decimal? SgstPer { get; set; }
    [JsonPropertyName("sgstAmount")]       [JsonProperty("sgstAmount")]       public decimal? SgstAmount { get; set; }
    [JsonPropertyName("cgstPer")]          [JsonProperty("cgstPer")]          public decimal? CgstPer { get; set; }
    [JsonPropertyName("cgstAmount")]       [JsonProperty("cgstAmount")]       public decimal? CgstAmount { get; set; }
    [JsonPropertyName("igstPer")]          [JsonProperty("igstPer")]          public decimal? IgstPer { get; set; }
    [JsonPropertyName("igstAmount")]       [JsonProperty("igstAmount")]       public decimal? IgstAmount { get; set; }
    [JsonPropertyName("discount")]         [JsonProperty("discount")]         public decimal? Discount { get; set; }
    [JsonPropertyName("mrp")]              [JsonProperty("mrp")]              public decimal? Mrp { get; set; }

    [JsonPropertyName("LbrDetl")] [JsonProperty("LbrDetl")]
    public List<MaterialTransferLaborDto> LbrDetl { get; set; } = new();
}

public class MaterialTransferLaborDto
{
    [JsonPropertyName("lbrIdno")]       [JsonProperty("lbrIdno")]       public long? LbrIdno { get; set; }
    [JsonPropertyName("lbrName")]       [JsonProperty("lbrName")]       public string? LbrName { get; set; }
    [JsonPropertyName("lbrDescription")][JsonProperty("lbrDescription")] public string? LbrDescription { get; set; }
    [JsonPropertyName("lbrRate")]       [JsonProperty("lbrRate")]       public decimal? LbrRate { get; set; }
    [JsonPropertyName("sgstPer")]       [JsonProperty("sgstPer")]       public decimal? SgstPer { get; set; }
    [JsonPropertyName("sgstAmount")]    [JsonProperty("sgstAmount")]    public decimal? SgstAmount { get; set; }
    [JsonPropertyName("cgstPer")]       [JsonProperty("cgstPer")]       public decimal? CgstPer { get; set; }
    [JsonPropertyName("cgstAmount")]    [JsonProperty("cgstAmount")]    public decimal? CgstAmount { get; set; }
    [JsonPropertyName("igstPer")]       [JsonProperty("igstPer")]       public decimal? IgstPer { get; set; }
    [JsonPropertyName("igstAmount")]    [JsonProperty("igstAmount")]    public decimal? IgstAmount { get; set; }
}

public class MaterialTransferDeleteRequest
{
    public List<int>? Ids { get; set; }
    public List<string>? UniqueKeys { get; set; }
}