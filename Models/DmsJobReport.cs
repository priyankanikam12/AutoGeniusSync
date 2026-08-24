using System;
namespace AutoGeniusSync.Models;

public partial class DmsJobReport
{
    public int Id { get; set; }

    public string? DealerName { get; set; }
    public string? DealerCode { get; set; }
    public string? DealerLocation { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }

    public string? JobNo { get; set; }
    public DateOnly? JobDate { get; set; }
    public string? JobType { get; set; }
    public string? ServiceHead { get; set; }
    public string? ServiceType { get; set; }
    public string? Kms { get; set; }

    public string? CustomerName { get; set; }
    public string? MobileNo { get; set; }
    public string? ChassisNo { get; set; }
    public string? RegNo { get; set; }
    public string? EngineNo { get; set; }
    public string? ItemName { get; set; }

    // Legacy single-complaint columns. Kept so old reports/queries that read
    // these two flat columns keep working — they are now populated from the
    // FIRST entry of Complaints below. The full set of complaints lives in
    // the child table.
    public string? CustomerVoice { get; set; }
    public string? ComplaintCode { get; set; }

    public string? Observation { get; set; }
    public string? SupervisorComment { get; set; }
    public string? JobStatus { get; set; }

    public string? BatteryNo { get; set; }
    public string? BrandName { get; set; }
    public string? ChargerNo { get; set; }
    public DateOnly? SaleDate { get; set; }

    public string? Supervisor { get; set; }
    public string? Technician { get; set; }
    public DateOnly? JobEndDate { get; set; }
    public string? CreatedThrough { get; set; }

    public string? UniqueKey { get; set; }
    public string? Action { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // NEW: one job card can have many customerVoice/complaintCode pairs.
    public List<DmsJobReportComplaint> Complaints { get; set; } = new();
}