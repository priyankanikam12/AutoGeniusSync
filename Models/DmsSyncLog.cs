using System;
using System.Collections.Generic;

namespace AutoGeniusSync.Models;

public partial class DmsSyncLog
{
    public int Id { get; set; }

    public string? SyncType { get; set; }

    public DateOnly SyncDate { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime CompletedAt { get; set; }

    public int? RecordsFetched { get; set; }

    public int? RecordsInserted { get; set; }

    public int? RecordsUpdated { get; set; }

    public string? Status { get; set; }

    public string? ErrorMessage { get; set; }

    public string? DealerCode { get; set; }
}
