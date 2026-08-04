using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/data")]
public class DataController : ControllerBase
{
    private readonly DataSyncService _sync;
    private readonly ILogger<DataController> _logger;

    public DataController(DataSyncService sync, ILogger<DataController> logger)
    {
        _sync = sync;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════
    // SERVICE HISTORY
    // ═══════════════════════════════════════════════════════

    // POST /api/data/service-history — insert-only, skips existing
    [HttpPost("service-history")]
    public async Task<IActionResult> PostServiceHistory(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.SyncServiceHistoryForRangeAsync(start, end);
        return Ok(result);
    }

    // PUT /api/data/service-history — upsert: insert new, update existing
    [HttpPut("service-history")]
    public async Task<IActionResult> PutServiceHistory(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.UpsertServiceHistoryForRangeAsync(start, end);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════
    // LINE ORDER REPORT (LOR)
    // ═══════════════════════════════════════════════════════

    // POST /api/data/lor — insert-only, skips existing
    [HttpPost("lor")]
    public async Task<IActionResult> PostLor(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.SyncLineOrderReportAsync(start, end);
        return Ok(result);
    }

    // PUT /api/data/lor — upsert: insert new, update existing
    [HttpPut("lor")]
    public async Task<IActionResult> PutLor(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.UpsertLineOrderReportAsync(start, end);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════
    // VEHICLE SALES (VSR)
    // ═══════════════════════════════════════════════════════

    // POST /api/data/vehicle-sales — insert-only, skips existing
    [HttpPost("vehicle-sales")]
    public async Task<IActionResult> PostVehicleSales(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.SyncVehicleSalesForRangeAsync(start, end);
        return Ok(result);
    }

    // PUT /api/data/vehicle-sales — upsert: insert new, update existing
    [HttpPut("vehicle-sales")]
    public async Task<IActionResult> PutVehicleSales(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.UpsertVehicleSalesForRangeAsync(start, end);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════
    // VEHICLE DISPATCH (VDR)
    // ═══════════════════════════════════════════════════════

    // POST /api/data/vehicle-dispatch — insert-only, skips existing
    [HttpPost("vehicle-dispatch")]
    public async Task<IActionResult> PostVehicleDispatch(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.SyncVehicleDispatchesForRangeAsync(start, end);
        return Ok(result);
    }

    // PUT /api/data/vehicle-dispatch — upsert: insert new, update existing
    [HttpPut("vehicle-dispatch")]
    public async Task<IActionResult> PutVehicleDispatch(
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var start = from ?? DateTime.UtcNow.Date;
        var end   = to   ?? DateTime.UtcNow.Date;
        var result = await _sync.UpsertVehicleDispatchesForRangeAsync(start, end);
        return Ok(result);
    }
}