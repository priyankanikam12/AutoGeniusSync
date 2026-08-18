using System.Text.Json.Serialization;
using AutoGeniusSync.Json;

namespace AutoGeniusSync.DTOs;

// NOTE: deliberately no Id property. Id is the database primary key — it's
// generated on insert and matched by UniqueKey on update, so it should never
// appear in the request body (and won't show up in the Swagger example below).
public class JobReportRecordDto
{
    // Ignored on input for bulk endpoints — the server always recomputes this
    // from DealerCode+JobNo+JobDate+ChassisNo. Also accepts numeric input
    // ("uniqueKey": 30261) so it doesn't fail JSON binding if the client
    // still sends its own external id in this field.
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? UniqueKey { get; set; }
    public string? DealerName { get; set; }
    public string? DealerCode { get; set; }
    public string? DealerLocation { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }

    // Payload sends this as a JSON number ("jobNo": 87) — accept string or number.
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? JobNo { get; set; }

    // Payload sends full ISO datetimes ("2026-08-13T00:00:00") — accept those or plain dates.
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

    // Legacy single-complaint fields. Kept for backward compatibility with
    // older callers / CSV upload rows that only ever carried one complaint.
    // New callers should use Complaints below instead.
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