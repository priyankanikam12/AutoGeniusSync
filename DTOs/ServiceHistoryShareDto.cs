namespace AutoGeniusSync.DTOs;

// Projection of DmsServiceHistory for external sharing — deliberately omits:
// BrandName, VehicleType, IndividualAhbattery1-6, Accessory, Oil, IsRowTotal.
// Everything else from the entity is included as-is.
public class ServiceHistoryShareDto
{
    public int Id { get; set; }

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
    public string? RegNo { get; set; }
    public string? EngineNo { get; set; }
    public string? ChassisNo { get; set; }
    public string? Kms { get; set; }

    public string? BatterySerialNo1 { get; set; }
    public string? BatterySerialNo2 { get; set; }
    public string? BatterySerialNo3 { get; set; }
    public string? BatterySerialNo4 { get; set; }
    public string? BatterySerialNo5 { get; set; }
    public string? BatterySerialNo6 { get; set; }

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
    public decimal? Labour { get; set; }
    public decimal? OutsideWork { get; set; }
    public decimal? TotalWotax { get; set; }
    public decimal? Gstamount { get; set; }
    public decimal? Igstamount { get; set; }
    public decimal? NetTotal { get; set; }

    public string? RepairType { get; set; }
    public DateOnly? CompletionDate { get; set; }
    public string? JobStatus { get; set; }

    public string? RowHash { get; set; }
    public string? UniqueKey { get; set; }

    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}