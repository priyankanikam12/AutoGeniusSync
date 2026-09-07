using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutoGeniusSync.Models
{
    public class DmsProformaItem
    {
        public int Id { get; set; }

        public int ProformaId { get; set; }

        [JsonIgnore]
        public DmsProforma? Proforma { get; set; }

        public long? SourceItemId { get; set; }   // payload "id"
        public long? ItemIdno { get; set; }        // payload "Item_Idno"
        public string? ItemCode { get; set; }
        public string? ItemDesc { get; set; }
        public string? ItemType { get; set; }       // "Parts" / "Labour"
        public int? Qty { get; set; }
        public decimal? Rate { get; set; }
        public string? IssueType { get; set; }      // "Paid" / "U/W" / "FOC" / "Goodwill"
        public decimal? SgstPer { get; set; }
        public decimal? SgstAmount { get; set; }
        public decimal? CgstPer { get; set; }
        public decimal? CgstAmount { get; set; }
        public decimal? IgstPer { get; set; }
        public decimal? IgstAmount { get; set; }
        public decimal? WavRate { get; set; }
        public decimal? TotAmnt { get; set; }        // payload "Tot_Amnt"
        public string? MtrlIssue { get; set; }       // payload "Mtrl_Issue" ("Yes"/"No")
    }
}
