using System;
using Newtonsoft.Json;

namespace AutoGeniusSync.Models;

// One customerVoice/complaintCode pair belonging to a job card (DmsJobReport).
// A single job card row can now have many of these instead of exactly one.
public class DmsJobReportComplaint
{
    public int Id { get; set; }

    public int JobReportId { get; set; }

    // [JsonIgnore] (Newtonsoft.Json, NOT System.Text.Json.Serialization) stops
    // the serializer walking back up to the parent JobReport, which in turn
    // has this same Complaints list — without this, Newtonsoft loops forever:
    // DmsJobReport -> Complaints -> JobReport -> Complaints -> ... and throws
    // JsonSerializationException: Self referencing loop detected.
    [JsonIgnore]
    public DmsJobReport? JobReport { get; set; }

    public string? CustomerVoice { get; set; }
    public string? ComplaintCode { get; set; }

    public DateTime CreatedAt { get; set; }
}