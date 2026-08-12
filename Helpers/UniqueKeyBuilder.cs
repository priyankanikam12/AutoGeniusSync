// Helpers/UniqueKeyBuilder.cs
namespace AutoGeniusSync.Helpers;

public static class UniqueKeyBuilder
{
    private static string Norm(string? s) => s?.Trim().ToUpperInvariant() ?? "";

    public static string ServiceHistory(string? dealerCode, string? jobNo, DateOnly? jobDate, string? chassisNo)
        => $"{Norm(dealerCode)}{Norm(jobNo)}{jobDate:yyyy-MM-dd}{Norm(chassisNo)}";

    public static string JobReport(string? dealerCode, string? jobNo, DateOnly? jobDate, string? chassisNo)
        => $"{Norm(dealerCode)}{Norm(jobNo)}{jobDate:yyyy-MM-dd}{Norm(chassisNo)}";

    public static string Lor(string? dealerCode, string? uniqueId, string? docNo, string? itemName)
        => $"{Norm(dealerCode)}{Norm(uniqueId)}{Norm(docNo)}{Norm(itemName)}";

    public static string VehicleSale(string? dealerCode, string? invoiceNo, string? chassisNo)
        => $"{Norm(dealerCode)}{Norm(invoiceNo)}{Norm(chassisNo)}";

    public static string VehicleDispatch(string? invoiceNo, string? chassisNo)
        => $"{Norm(invoiceNo)}{Norm(chassisNo)}";
}