namespace AutoGeniusSync.Models;

// Common shape of the four ERP report tables, used only in C# code by the batched UPSERT in
// DataSyncService.Checkpoint.cs (never inside an EF query, so no translation concerns).
public interface IErpSyncRow
{
    int Id { get; set; }
    string? UniqueKey { get; set; }
    string? RowHash { get; set; }
    string? ChassisNo { get; set; }
    DateTime? CreatedAt { get; set; }
    DateTime? UpdatedAt { get; set; }
}

public partial class DmsServiceHistory  : IErpSyncRow { }
public partial class DmsVehicleSale     : IErpSyncRow { }
public partial class DmsVehicleDispatch : IErpSyncRow { }
public partial class DmsLineOrderReport : IErpSyncRow { }
