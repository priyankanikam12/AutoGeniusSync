using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutoGeniusSync.DTOs
{
    // Response shape for GET api/anvweb/maintenance and api/anvweb/maintenance/lookup
    // — field names and casing (snake_case) match the partner's own integration
    // spec exactly, so their team can deserialize this with zero field mapping.
    //
    // HONEST GAPS — read this before sharing a sample payload as "done":
    // ExpectedRfd, VehicleAtHub, PartnerRemark, and AssessmentStatus are NOT
    // tracked anywhere in AutoGeniusSync. They come back null/false-by-default
    // as noted below — see the `_meta` block on the envelope response for the
    // full list. MaintenanceType and FinalStatus are inferred, not real workflow
    // fields:
    //   - MaintenanceType: guessed from RepairType/JobType/JobCategory text
    //     containing "insur" -> insurance, "running" -> running_repair,
    //     "repair" -> repair, else -> service.
    //   - FinalStatus: "ready_for_dispatch" if the vehicle's latest service job
    //     is Closed, otherwise "in_progress".
    //   - AssessmentStatus: "assessed" if we've raised a Proforma (estimate) for
    //     this vehicle, otherwise "pending" — a proxy, not a real assessment flag.
    //   - HubLocation: the dealer/service Location on file, not a hub-network
    //     concept — approximate at best.
    public class MaintenanceVehicleDto
    {
        [JsonPropertyName("registration_number")]
        public string? RegistrationNumber { get; set; }

        [JsonPropertyName("chassis_number")]
        public string? ChassisNumber { get; set; }

        [JsonPropertyName("maintenance_type")]
        public string? MaintenanceType { get; set; } // repair | service | insurance | running_repair

        [JsonPropertyName("expected_rfd")]
        public DateOnly? ExpectedRfd { get; set; } // always null — not tracked in AutoGeniusSync

        [JsonPropertyName("vehicle_at_hub")]
        public bool? VehicleAtHub { get; set; } // always null — not tracked in AutoGeniusSync

        [JsonPropertyName("hub_location")]
        public string? HubLocation { get; set; }

        [JsonPropertyName("partner_remark")]
        public string? PartnerRemark { get; set; } // always null — not tracked in AutoGeniusSync

        [JsonPropertyName("assessment_status")]
        public string? AssessmentStatus { get; set; } // assessed | pending (proxy — see class doc)

        [JsonPropertyName("final_status")]
        public string? FinalStatus { get; set; } // in_progress | ready_for_dispatch

        [JsonPropertyName("parts_required")]
        public List<MaintenancePartDto> PartsRequired { get; set; } = new();

        [JsonPropertyName("invoices")]
        public List<MaintenanceInvoiceDto> Invoices { get; set; } = new();

        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }
    }

    public class MaintenancePartDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("part_no")]
        public string? PartNo { get; set; }

        [JsonPropertyName("unit_price")]
        public decimal? UnitPrice { get; set; }

        [JsonPropertyName("qty")]
        public int? Qty { get; set; }

        [JsonPropertyName("insurance_covered")]
        public bool? InsuranceCovered { get; set; } // always null — no reliable source field for this

        [JsonPropertyName("warranty_covered")]
        public bool? WarrantyCovered { get; set; } // inferred from IssueType == "U/W" — see controller comment

        [JsonPropertyName("available")]
        public bool? Available { get; set; } // looked up against DMS_StockSummaryDetail.BalQty by (DealerCode, PartNo); null if no stock record found for that part
    }

    public class MaintenanceInvoiceDto
    {
        [JsonPropertyName("invoice_no")]
        public string? InvoiceNo { get; set; }

        [JsonPropertyName("amount")]
        public decimal? Amount { get; set; }

        [JsonPropertyName("date")]
        public DateOnly? Date { get; set; }

        [JsonPropertyName("file_url")]
        public string? FileUrl { get; set; } // always null — no document storage in AutoGeniusSync
    }
}