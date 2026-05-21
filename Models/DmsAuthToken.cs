using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class DmsAuthToken
{
    public int Id { get; set; }

    public string AccessToken { get; set; } = null!;

    public string? LoginEmail { get; set; }

    public string? VendorName { get; set; }

    public string? VendorCode { get; set; }

    public string? VendorId { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsActive { get; set; }
}
