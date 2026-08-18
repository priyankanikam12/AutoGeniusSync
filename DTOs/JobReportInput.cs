using System.Text.Json.Serialization;
using AutoGeniusSync.Json;

namespace AutoGeniusSync.DTOs;

// Used for POST (create) and PUT (full update) request bodies.
// Deliberately has NO Id property — Id is server-generated on create
// and taken from the route parameter on update, never from the body.
public class JobReportInput
{
    public string? DealerName { get; set; }
    public string? DealerCode { get; set; }
    public string? DealerLocation { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? JobNo { get; set; }

    [JsonConverter(typeof(FlexibleDateOnlyConverter))]
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

    // Legacy single-complaint fields, kept for backward compatibility.
    // Prefer Complaints below for new callers.
    public string? CustomerVoice { get; set; }
    public string? ComplaintCode { get; set; }

    public string? Observation { get; set; }
    public string? SupervisorComment { get; set; }
    public string? JobStatus { get; set; }

    public string? BatteryNo { get; set; }
    public string? BrandName { get; set; }
    public string? ChargerNo { get; set; }

    [JsonConverter(typeof(FlexibleDateOnlyConverter))]
    public DateOnly? SaleDate { get; set; }

    public string? Supervisor { get; set; }
    public string? Technician { get; set; }

    [JsonConverter(typeof(FlexibleDateOnlyConverter))]
    public DateOnly? JobEndDate { get; set; }

    public string? CreatedThrough { get; set; }

    // NEW: multiple customerVoice/complaintCode pairs for one job card.
    public List<ComplaintDto>? Complaints { get; set; }
}