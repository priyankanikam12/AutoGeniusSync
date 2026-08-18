namespace AutoGeniusSync.DTOs;

// One customerVoice/complaintCode pair inside a job card's "Complaints" array.
public class ComplaintDto
{
    public string? CustomerVoice { get; set; }
    public string? ComplaintCode { get; set; }
}