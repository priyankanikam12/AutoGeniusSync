// DTOs/ServiceHistoryImportDto.cs
namespace AutoGeniusSync.DTOs;

public class ServiceHistoryImportRequest
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ServiceHistoryRecordDto> Records { get; set; } = new();
}