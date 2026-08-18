using System;

namespace AutoGeniusSync.Models;

// One customerVoice/complaintCode pair belonging to a job card (DmsJobReport).
// A single job card row can now have many of these instead of exactly one.
public class DmsJobReportComplaint
{
    public int Id { get; set; }

    public int JobReportId { get; set; }
    public DmsJobReport? JobReport { get; set; }

    public string? CustomerVoice { get; set; }
    public string? ComplaintCode { get; set; }

    public DateTime CreatedAt { get; set; }
}