using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class DmsPincodeMaster
{
    public int Id { get; set; }

    public string PinCode { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime? CreatedAt { get; set; }
}
