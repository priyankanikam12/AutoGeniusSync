namespace AutoGeniusSync.DTOs;

public class ServiceHistoryRecordDto
{
    public int? Id { get; set; }               // ignored on write, accepted so client payload doesn't need trimming
    public string? DealerCode { get; set; }
    public string? JobNo { get; set; }
    public DateOnly? JobDate { get; set; }
    public string? CompName { get; set; }
    public string? Location { get; set; }
    public string? InTime { get; set; }
    public string? CloseTime { get; set; }
    public string? JobCategory { get; set; }
    public string? Ffrpercentage { get; set; }
    public string? DocNo { get; set; }
    public string? DocType { get; set; }
    public DateOnly? DocDate { get; set; }
    public string? Model { get; set; }
    public string? BrandName { get; set; }
    public string? RegNo { get; set; }
    public string? VehicleType { get; set; }
    public string? EngineNo { get; set; }
    public string? ChassisNo { get; set; }
    public string? Kms { get; set; }
    public string? BatterySerialNo1 { get; set; }
    public string? BatterySerialNo2 { get; set; }
    public string? BatterySerialNo3 { get; set; }
    public string? BatterySerialNo4 { get; set; }
    public string? BatterySerialNo5 { get; set; }
    public string? BatterySerialNo6 { get; set; }
    public string? IndividualAhbattery1 { get; set; }
    public string? IndividualAhbattery2 { get; set; }
    public string? IndividualAhbattery3 { get; set; }
    public string? IndividualAhbattery4 { get; set; }
    public string? IndividualAhbattery5 { get; set; }
    public string? IndividualAhbattery6 { get; set; }
    public string? PartyName { get; set; }
    public string? MobileNumber { get; set; }
    public string? Supervisor { get; set; }
    public string? Technician { get; set; }
    public string? ServiceHead { get; set; }
    public string? JobType { get; set; }
    public DateOnly? SaleDate { get; set; }
    public string? CouponNo { get; set; }
    public DateOnly? ExpectedDeliveryDate { get; set; }
    public DateOnly? ProformaDate { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public decimal? EstimatedJobExpenses { get; set; }
    public decimal? LabourHours { get; set; }
    public decimal? Parts { get; set; }
    public decimal? Accessory { get; set; }
    public decimal? Oil { get; set; }
    public decimal? Labour { get; set; }
    public decimal? OutsideWork { get; set; }
    public decimal? TotalWotax { get; set; }
    public decimal? Gstamount { get; set; }
    public decimal? Igstamount { get; set; }
    public decimal? NetTotal { get; set; }
    public bool IsRowTotal { get; set; }
    public string? RepairType { get; set; }
    public DateOnly? CompletionDate { get; set; }
    // JobStatus intentionally omitted — DB-computed
    public string? RowHash { get; set; }
    public string? UniqueKey { get; set; }
}