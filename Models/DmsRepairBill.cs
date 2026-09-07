using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutoGeniusSync.Models
{
    // Header record — one row per RepairBill document.
    // Same shape as DmsProforma (the source payloads are structurally identical),
    // kept as a separate table since RepairBill and Proforma are separate document types.
    public class DmsRepairBill
    {
        public int Id { get; set; }

        public string? DealerName { get; set; }
        public string? DealerCode { get; set; }
        public long? UniqueKey { get; set; }
        public long? UniqueId { get; set; }
        public long? InvoiceNo { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public string? Location { get; set; }
        public string? PartyName { get; set; }
        public int? BillType { get; set; }
        public string? CashType { get; set; }       // payload "Cash_Type"
        public string? CashAccount { get; set; }
        public string? RegNo { get; set; }
        public string? ChassisNo { get; set; }

        // Server-computed dedup key: "{UniqueId}_{UniqueKey}" — see Helpers/UniqueKeyBuilder.RepairBill
        public string ComputedUniqueKey { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public ICollection<DmsRepairBillItem> Items { get; set; } = new List<DmsRepairBillItem>();
    }
}