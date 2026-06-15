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
    }
}