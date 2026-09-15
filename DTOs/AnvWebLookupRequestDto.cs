using System.Text.Json.Serialization;

namespace AutoGeniusSync.DTOs
{
    // Request body for POST api/anvweb/lookup.
    // Provide at least one of ChassisNo / RegistrationNo. If both are given,
    // ChassisNo takes priority (RegistrationNo is only used to resolve a
    // chassis number when ChassisNo isn't supplied).
    public class AnvWebLookupRequestDto
    {
        [JsonPropertyName("chassisNo")]
        public string? ChassisNo { get; set; }

        [JsonPropertyName("registrationNo")]
        public string? RegistrationNo { get; set; }
    }
}
