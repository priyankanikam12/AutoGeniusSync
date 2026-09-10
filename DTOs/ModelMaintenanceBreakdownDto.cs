using System.Text.Json.Serialization;

namespace AutoGeniusSync.DTOs
{
    // One row of the EV-model × maintenance-type breakdown table, matching the
    // "Repair | Service | Insurance" x "0-3 / 4-7 / 8-15 / 16-30 / 30+" grid shape
    // ANV Web Ventures' dashboard already shows for their own vehicles.
    public class ModelMaintenanceBreakdownDto
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "Unknown";

        [JsonPropertyName("repair")]
        public DayBucketCountsDto Repair { get; set; } = new();

        [JsonPropertyName("service")]
        public DayBucketCountsDto Service { get; set; } = new();

        [JsonPropertyName("insurance")]
        public DayBucketCountsDto Insurance { get; set; } = new();
    }

    public class DayBucketCountsDto
    {
        [JsonPropertyName("d0to3")]
        public int D0to3 { get; set; }

        [JsonPropertyName("d4to7")]
        public int D4to7 { get; set; }

        [JsonPropertyName("d8to15")]
        public int D8to15 { get; set; }

        [JsonPropertyName("d16to30")]
        public int D16to30 { get; set; }

        [JsonPropertyName("d30plus")]
        public int D30Plus { get; set; }

        [JsonPropertyName("total")]
        public int Total => D0to3 + D4to7 + D8to15 + D16to30 + D30Plus;

        public void Add(int days)
        {
            if (days <= 3) D0to3++;
            else if (days <= 7) D4to7++;
            else if (days <= 15) D8to15++;
            else if (days <= 30) D16to30++;
            else D30Plus++;
        }
    }
}