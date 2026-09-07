using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs
{
    // Matches one entry of the payload's "ProformaDetl" array exactly.
    public class ProformaDetailDto
    {
        [JsonPropertyName("id")]
        [JsonProperty("id")]
        public long? Id { get; set; }

        [JsonPropertyName("Item_Idno")]
        [JsonProperty("Item_Idno")]
        public long? ItemIdno { get; set; }

        [JsonPropertyName("itemCode")]
        [JsonProperty("itemCode")]
        public string? ItemCode { get; set; }

        [JsonPropertyName("itemDesc")]
        [JsonProperty("itemDesc")]
        public string? ItemDesc { get; set; }

        [JsonPropertyName("itemType")]
        [JsonProperty("itemType")]
        public string? ItemType { get; set; }

        [JsonPropertyName("qty")]
        [JsonProperty("qty")]
        public int? Qty { get; set; }

        [JsonPropertyName("rate")]
        [JsonProperty("rate")]
        public decimal? Rate { get; set; }

        [JsonPropertyName("issueType")]
        [JsonProperty("issueType")]
        public string? IssueType { get; set; }

        [JsonPropertyName("sgstPer")]
        [JsonProperty("sgstPer")]
        public decimal? SgstPer { get; set; }

        [JsonPropertyName("sgstAmount")]
        [JsonProperty("sgstAmount")]
        public decimal? SgstAmount { get; set; }

        [JsonPropertyName("cgstPer")]
        [JsonProperty("cgstPer")]
        public decimal? CgstPer { get; set; }

        [JsonPropertyName("cgstAmount")]
        [JsonProperty("cgstAmount")]
        public decimal? CgstAmount { get; set; }

        [JsonPropertyName("igstPer")]
        [JsonProperty("igstPer")]
        public decimal? IgstPer { get; set; }

        [JsonPropertyName("igstAmount")]
        [JsonProperty("igstAmount")]
        public decimal? IgstAmount { get; set; }

        [JsonPropertyName("wavRate")]
        [JsonProperty("wavRate")]
        public decimal? WavRate { get; set; }

        [JsonPropertyName("Tot_Amnt")]
        [JsonProperty("Tot_Amnt")]
        public decimal? TotAmnt { get; set; }

        [JsonPropertyName("Mtrl_Issue")]
        [JsonProperty("Mtrl_Issue")]
        public string? MtrlIssue { get; set; }
    }

    // Matches one header object of the payload array exactly.
    public class ProformaRecordDto
    {
        [JsonPropertyName("dealerName")]
        [JsonProperty("dealerName")]
        public string? DealerName { get; set; }

        [JsonPropertyName("dealerCode")]
        [JsonProperty("dealerCode")]
        public string? DealerCode { get; set; }

        [JsonPropertyName("uniqueKey")]
        [JsonProperty("uniqueKey")]
        public long? UniqueKey { get; set; }

        [JsonPropertyName("uniqueId")]
        [JsonProperty("uniqueId")]
        public long? UniqueId { get; set; }

        [JsonPropertyName("invoiceNo")]
        [JsonProperty("invoiceNo")]
        public long? InvoiceNo { get; set; }

        [JsonPropertyName("invoiceDate")]
        [JsonProperty("invoiceDate")]
        public DateTime? InvoiceDate { get; set; }

        [JsonPropertyName("location")]
        [JsonProperty("location")]
        public string? Location { get; set; }

        [JsonPropertyName("partyName")]
        [JsonProperty("partyName")]
        public string? PartyName { get; set; }

        [JsonPropertyName("billType")]
        [JsonProperty("billType")]
        public int? BillType { get; set; }

        [JsonPropertyName("Cash_Type")]
        [JsonProperty("Cash_Type")]
        public string? CashType { get; set; }

        [JsonPropertyName("cashAccount")]
        [JsonProperty("cashAccount")]
        public string? CashAccount { get; set; }

        [JsonPropertyName("regNo")]
        [JsonProperty("regNo")]
        public string? RegNo { get; set; }

        [JsonPropertyName("chassisNo")]
        [JsonProperty("chassisNo")]
        public string? ChassisNo { get; set; }

        [JsonPropertyName("ProformaDetl")]
        [JsonProperty("ProformaDetl")]
        public List<ProformaDetailDto> ProformaDetl { get; set; } = new();
    }

    // Body for DELETE api/proforma/bulk — identifies rows by the same 2-field key used to dedupe on insert.
    public class ProformaDeleteRequestDto
    {
        [JsonPropertyName("uniqueId")]
        [JsonProperty("uniqueId")]
        public long? UniqueId { get; set; }

        [JsonPropertyName("uniqueKey")]
        [JsonProperty("uniqueKey")]
        public long? UniqueKey { get; set; }
    }
}
