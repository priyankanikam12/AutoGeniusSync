using System.Collections.Generic;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace AutoGeniusSync.DTOs
{
    // Input shape for POST bulk-insert / PUT bulk-upsert / PUT {id} on
    // api/stocksummary. Field names mirror DmsStockSummaryDetail exactly (which in
    // turn mirrors the ERP "Stock Summary Detail Report" sheet headers) except
    // PartDescription (renamed from the sheet's "Description") and DealerCode/
    // DealerName/LocCode/ReportOption (not in the sheet itself — supply them from
    // the import context, e.g. which dealer this file was exported for).
    //
    // Dual [JsonPropertyName]/[JsonProperty] attributes are on every property,
    // same as the Proforma/RepairBill DTOs elsewhere in this codebase, since it
    // wasn't clear which of System.Text.Json / Newtonsoft.Json is the active
    // provider — whichever it is will pick up its matching attribute.
    public class StockSummaryDetailDto
    {
        [JsonPropertyName("dealerCode")] [JsonProperty("dealerCode")]
        public string? DealerCode { get; set; }

        [JsonPropertyName("dealerName")] [JsonProperty("dealerName")]
        public string? DealerName { get; set; }

        [JsonPropertyName("locCode")] [JsonProperty("locCode")]
        public string? LocCode { get; set; }

        [JsonPropertyName("reportOption")] [JsonProperty("reportOption")]
        public string? ReportOption { get; set; }

        [JsonPropertyName("partNo")] [JsonProperty("partNo")]
        public string PartNo { get; set; } = null!;

        [JsonPropertyName("partDescription")] [JsonProperty("partDescription")]
        public string? PartDescription { get; set; }

        [JsonPropertyName("hsnsacCode")] [JsonProperty("hsnsacCode")]
        public string? HSNSAC_Code { get; set; }

        [JsonPropertyName("groupName")] [JsonProperty("groupName")]
        public string? GroupName { get; set; }

        [JsonPropertyName("itemType")] [JsonProperty("itemType")]
        public string? ItemType { get; set; }

        [JsonPropertyName("openBal")] [JsonProperty("openBal")]
        public int OpenBal { get; set; }

        [JsonPropertyName("purchase")] [JsonProperty("purchase")]
        public int Purchase { get; set; }

        [JsonPropertyName("receipt")] [JsonProperty("receipt")]
        public int Receipt { get; set; }

        [JsonPropertyName("ppurchln")] [JsonProperty("ppurchln")]
        public int PPurChln { get; set; }

        [JsonPropertyName("total")] [JsonProperty("total")]
        public int Total { get; set; }

        [JsonPropertyName("sale")] [JsonProperty("sale")]
        public int Sale { get; set; }

        [JsonPropertyName("briss")] [JsonProperty("briss")]
        public int BrIss { get; set; }

        [JsonPropertyName("mtrliss")] [JsonProperty("mtrliss")]
        public int MtrlIss { get; set; }

        [JsonPropertyName("stadj")] [JsonProperty("stadj")]
        public int StAdj { get; set; }

        [JsonPropertyName("partschln")] [JsonProperty("partschln")]
        public int PartsChln { get; set; }

        [JsonPropertyName("purreturn")] [JsonProperty("purreturn")]
        public int PurReturn { get; set; }

        [JsonPropertyName("salereturn")] [JsonProperty("salereturn")]
        public int SaleReturn { get; set; }

        [JsonPropertyName("salechln")] [JsonProperty("salechln")]
        public int SaleChln { get; set; }

        [JsonPropertyName("balQty")] [JsonProperty("balQty")]
        public int BalQty { get; set; }

        [JsonPropertyName("balAmnt")] [JsonProperty("balAmnt")]
        public decimal BalAmnt { get; set; }

        [JsonPropertyName("qtyReqd")] [JsonProperty("qtyReqd")]
        public int QtyReqd { get; set; }

        [JsonPropertyName("minOrder")] [JsonProperty("minOrder")]
        public int MinOrder { get; set; }

        [JsonPropertyName("billPrice")] [JsonProperty("billPrice")]
        public decimal BillPrice { get; set; }
    }

    // Body for DELETE api/stocksummary/bulk-delete — either a list of Ids, or a
    // DealerCode to wipe that dealer's whole stock snapshot before a fresh import.
    public class BulkDeleteRequestDto
    {
        [JsonPropertyName("ids")] [JsonProperty("ids")]
        public List<int>? Ids { get; set; }

        [JsonPropertyName("dealerCode")] [JsonProperty("dealerCode")]
        public string? DealerCode { get; set; }
    }
}