using System;

namespace AutoGeniusSync.Models
{
    // One row per (DealerCode, PartNo) — a dealer's current spares/stock position
    // for that part, as reported by the ERP's "Stock Summary Detail Report" (the
    // uploaded file: "Stock Summary Detail Report from 01-04-2026 to 15-09-2026 of
    // MAGNEMITE MOTO LLP-DELHI dated 2026-Sep-15 15:42").
    //
    // DESIGN CHOICE (flagging so you can push back if wrong): the report is a live
    // snapshot — BalQty/BalAmnt etc. are "as of" whatever date the ERP was queried
    // with, which moves forward every time it's re-pulled, the same part number
    // appearing with different balances each time. So this table is UPSERTED per
    // (DealerCode, PartNo) — same pattern as DmsDealer — each sync overwrites that
    // part's row with its latest figures, rather than appending a new row per
    // import like DmsServiceHistory/DmsLineOrderReport do for transactional jobs.
    //
    // DealerCode/DealerName/LocCode aren't columns in the sheet itself — the sheet
    // is one dealer's export — so the import code needs to supply them (from the
    // filename/request, same as how UploadMaster infers dealer context today).
    public partial class DmsStockSummaryDetail
    {
        public int Id { get; set; }

        // Dealer context (not in the sheet — supplied by the importer)
        public string? DealerCode { get; set; }
        public string? DealerName { get; set; }
        public string? LocCode { get; set; }

        // Which "Report Option" the ERP screen was set to when this snapshot was
        // pulled: "Include Negative" / "Exclude Negative" / "Only Negative" (refers
        // to parts with a negative BalQty). Recorded here for traceability; the
        // live GET endpoint's own ?reportOption= filter re-applies the same logic
        // against BalQty rather than trusting this stored value, so it works
        // correctly even for rows synced before this field existed.
        public string? ReportOption { get; set; }

        // Item identity — straight from the sheet
        public string PartNo { get; set; } = null!;
        public string? PartDescription { get; set; } // renamed from the sheet's "Description"
        public string? HSNSAC_Code { get; set; }
        public string? GroupName { get; set; } // sheet header: "Group Name"
        public string? ItemType { get; set; }  // sheet header: "Item Type"

        // Stock movement figures — column names match the sheet's own header row
        public int OpenBal { get; set; }
        public int Purchase { get; set; }
        public int Receipt { get; set; }
        public int PPurChln { get; set; }
        public int Total { get; set; }
        public int Sale { get; set; }
        public int BrIss { get; set; }
        public int MtrlIss { get; set; }
        public int StAdj { get; set; }
        public int PartsChln { get; set; }
        public int PurReturn { get; set; }
        public int SaleReturn { get; set; }
        public int SaleChln { get; set; }
        public int BalQty { get; set; }
        public decimal BalAmnt { get; set; }
        public int QtyReqd { get; set; }
        public int MinOrder { get; set; }
        public decimal BillPrice { get; set; }

        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? RowHash { get; set; }
        public string? UniqueKey { get; set; } // DealerCode + "|" + PartNo
    }
}