namespace AutoGeniusSync.DTOs
{
    public class ShadowfaxVehicleDto
    {
        public string? ChassisNo { get; set; }
        public string? JobNo { get; set; }
        public string? RegNo { get; set; }
        public string? Model { get; set; }
        public DateOnly? JobcardCreationDate { get; set; }
        public DateOnly? CompletionDate { get; set; }
        public string? RepairType { get; set; }
        public string? Status { get; set; }

        // ── Fields that were missing ──────────────────────
        public string? DealerCode { get; set; }
        public string? DealerName { get; set; }
        public string? PartyName { get; set; }
        public string? MobileNumber { get; set; }
        public string? DocNo { get; set; }
        public string? DocType { get; set; }
        public decimal? NetTotal { get; set; }
        public string? Location { get; set; }
        public string? PaymentMode { get; set; }
    }
}