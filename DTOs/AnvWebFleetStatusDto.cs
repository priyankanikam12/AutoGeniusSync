using System;
using System.Text.Json.Serialization;

namespace AutoGeniusSync.DTOs
{
    // Output shape for the ANV Web Ventures partner dashboard feed.
    // Combines fields sourced from three AutoGeniusSync tables (see AnvWebController):
    // DmsVehicleSale (ownership/sale), DmsVehicleDispatch (vehicle master), and the
    // most recent DmsServiceHistory row per chassis (current repair/service status).
    public class AnvWebFleetStatusDto
    {
        [JsonPropertyName("chassisNo")]
        public string? ChassisNo { get; set; }

        [JsonPropertyName("registrationNo")]
        public string? RegistrationNo { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("soldTo")]
        public string? SoldTo { get; set; }

        [JsonPropertyName("saleDate")]
        public DateOnly? SaleDate { get; set; }

        [JsonPropertyName("vehicleStatus")]
        public string? VehicleStatus { get; set; }          // from DmsVehicleDispatch, e.g. "Sold"

        [JsonPropertyName("totalServiceJobs")]
        public int TotalServiceJobs { get; set; }

        [JsonPropertyName("latestJobNo")]
        public string? LatestJobNo { get; set; }

        [JsonPropertyName("latestJobDate")]
        public DateOnly? LatestJobDate { get; set; }

        [JsonPropertyName("latestJobType")]
        public string? LatestJobType { get; set; }

        [JsonPropertyName("latestJobStatus")]
        public string? LatestJobStatus { get; set; }          // DB-computed: "Open"/"Closed" style, see DmsServiceHistory.JobStatus

        [JsonPropertyName("latestCompletionDate")]
        public DateOnly? LatestCompletionDate { get; set; }

        [JsonPropertyName("latestRepairType")]
        public string? LatestRepairType { get; set; }
    }
}