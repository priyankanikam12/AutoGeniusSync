using System.ComponentModel.DataAnnotations.Schema;

namespace AutoGeniusSync.Models;

[Table("DMS_ShadowfaxChassisMaster")]
public partial class DmsShadowfaxChassisMaster
{
    public int Id { get; set; }
    public string? VehicleId { get; set; }
    public string? Model { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? ChassisNo { get; set; }
    public string? City { get; set; }
    public string? VehicleStatus { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}