using System;
using System.Text.Json.Serialization;

namespace AutoGeniusSync.DTOs
{
    // Response for POST api/anvweb/lookup.
    //
    // Field names deliberately mirror the RIDEV "Repair & Insurance Reports" grid
    // Balaji's team is working from (Registration No, Bike Name, Location, Status,
    // Chassis Number, Last Subscription Date, Date of Reporting, Actual Date of RFD,
    // Expected Date of RFD, Days Delayed, Reason) so their team can map this 1:1
    // against what they originally asked for.
    //
    // HONEST LIMITATION (read this before wiring this up as "the" fix):
    // AutoGeniusSync's schema has no current_status enum (repair / service_center /
    // insurance / un_allocated), no RFD target dates, no days-delayed calculation,
    // and no subscription dates — none of that exists anywhere in this database.
    // Those fields live only in the separate RIDEV (admin.ridev.in) system.
    //
    //   - LastSubscriptionDate, ExpectedDateOfRfd, DaysDelayed: ALWAYS null here.
    //     Listed in UnavailableFields on every response.
    //   - Status: only "Open" / "Closed" / "No service history" (from the vehicle's
    //     most recent DmsServiceHistory job), NOT the repair/service_center/
    //     insurance/un_allocated categorization RIDEV shows.
    //   - DateOfReporting: the service job's JobDate (closest equivalent to "when
    //     this vehicle was reported in").
    //   - ActualDateOfRfd: the service job's CompletionDate, only populated once
    //     the job is closed.
    //   - Reason: the job's RepairType/JobType text, not RIDEV's Reason field.
    //
    // These are the closest equivalents AutoGeniusSync has — not the same
    // underlying data RIDEV tracks. The Note field explains this to the caller
    // on every response so it's visible in the API itself, not just here.
    public class AnvWebLookupResponseDto
    {
        [JsonPropertyName("registrationNo")]
        public string? RegistrationNo { get; set; }

        [JsonPropertyName("bikeName")]
        public string? BikeName { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("chassisNumber")]
        public string? ChassisNumber { get; set; }

        [JsonPropertyName("lastSubscriptionDate")]
        public DateOnly? LastSubscriptionDate { get; set; }

        [JsonPropertyName("dateOfReporting")]
        public DateOnly? DateOfReporting { get; set; }

        [JsonPropertyName("actualDateOfRfd")]
        public DateOnly? ActualDateOfRfd { get; set; }

        [JsonPropertyName("expectedDateOfRfd")]
        public DateOnly? ExpectedDateOfRfd { get; set; }

        [JsonPropertyName("daysDelayed")]
        public int? DaysDelayed { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("unavailableFields")]
        public string[] UnavailableFields { get; set; } = Array.Empty<string>();

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}