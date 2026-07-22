using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class DmsDealer
{
    public int Id { get; set; }

    public string? DealerCode { get; set; }

    public string? DealerCompany { get; set; }

    public string? ContactNo { get; set; }

    public string? AlternateContactNo { get; set; }

    public string? DealerStateName { get; set; }

    public string? DealerCityName { get; set; }

    public string? PinCode { get; set; }

    public string? ActiveStatus { get; set; }

    public DateTime? LastFetchedAt { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // ── NEW: fields available from the ERP's ManageDealerReport export ──
    public string? Email { get; set; }
    public string? CityTier { get; set; }
    public string? Source { get; set; }
    public DateOnly? RegistrationDate { get; set; }
    public string? PreviousStatus { get; set; }
    public DateOnly? StatusModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public string? Remarks { get; set; }
}