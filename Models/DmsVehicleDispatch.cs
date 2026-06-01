using System.ComponentModel.DataAnnotations.Schema;

namespace AutoGeniusSync.Models;

// ─────────────────────────────────────────────────────────────
// DMS_VehicleDispatches
// ─────────────────────────────────────────────────────────────
[Table("DMS_VehicleDispatches")]
public partial class DmsVehicleDispatch
{
    public int Id { get; set; }
    public DateOnly? SaleDate { get; set; }
    public string? InvoiceNo { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public string? Location { get; set; }
    public string? LocationCode { get; set; }
    public string? LocationCity { get; set; }
    public string? LocationStatus { get; set; }
    public string? DealerName { get; set; }
    public string? Zone { get; set; }
    public string? AreaOffice { get; set; }
    public string? MfgYear { get; set; }
    public string? BrandName { get; set; }
    public string? ModelCode { get; set; }
    public string? ColorCode { get; set; }
    public string? ChassisNo { get; set; }
    public string? RegNo { get; set; }
    public string? MotorNo { get; set; }
    public string? BatteryId { get; set; }
    public string? BatteryNo { get; set; }
    public string? EcuSerialNo { get; set; }
    public string? EcuImEi { get; set; }
    public string? EcuBalMac { get; set; }
    public string? ImmoblizerNo { get; set; }
    public string? BikeSimId { get; set; }
    public string? BikeMobileNo { get; set; }
    public string? ChargerNo { get; set; }
    public string? ControllerNo { get; set; }
    public string? SoundbarSerialNo { get; set; }
    public string? SoundbarBalMac { get; set; }
    public string? Voltage { get; set; }
    public string? RegNumber { get; set; }
    public DateOnly? StartDate { get; set; }
    public string? Tyre1 { get; set; }
    public string? Tyre2 { get; set; }
    public string? VehicleStatus { get; set; }
    public string? BookingId { get; set; }
    public string? BillNo { get; set; }
    public DateOnly? BillDate { get; set; }
    public string? BillType { get; set; }
    public string? FinancerName { get; set; }
    public decimal FinAmount { get; set; }
    public string? NameOfParty { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? State { get; set; }
    public string? City { get; set; }
    public string? Pin { get; set; }
    public string? MobileNo { get; set; }
    public string? Email { get; set; }
    public string? AppPush { get; set; }
    public string? LeadId { get; set; }
    public string? Vcu { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}