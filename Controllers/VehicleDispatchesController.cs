using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

namespace AutoGeniusSync.Controllers;

// ─────────────────────────────────────────────────────────────
// Vehicle Dispatches Controller
// ─────────────────────────────────────────────────────────────
[ApiController]
[Route("api/[controller]")]
public class VehicleDispatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataSyncService _sync;

    public VehicleDispatchesController(AppDbContext db, DataSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    // GET /api/vehicledispatches?from=2026-01-01&to=2026-05-28
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? chassisNo,
        [FromQuery] string? vehicleStatus,
        [FromQuery] string? locationCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsVehicleDispatches.AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(v => v.ChassisNo != null && v.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(vehicleStatus))
            q = q.Where(v => v.VehicleStatus == vehicleStatus);

        if (!string.IsNullOrEmpty(locationCode))
            q = q.Where(v => v.LocationCode == locationCode);

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(v => v.SaleDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(v => v.SaleDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(v => v.SaleDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // GET /api/vehicledispatches/chassis/P6DSC12SPBB002162
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
        => Ok(await _db.DmsVehicleDispatches
            .Where(v => v.ChassisNo == chassisNo)
            .OrderByDescending(v => v.SaleDate)
            .ToListAsync());

    // GET /api/vehicledispatches/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleDispatches.AsQueryable();
        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.SaleDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.SaleDate <= t);

        var summary = await q.GroupBy(v => v.LocationCode)
            .Select(g => new
            {
                LocationCode  = g.Key,
                DealerName    = g.Select(x => x.DealerName).FirstOrDefault(),
                TotalDispatched = g.Count(),
                Sold          = g.Count(x => x.VehicleStatus == "Sold"),
                InStock       = g.Count(x => x.VehicleStatus != "Sold")
            })
            .OrderByDescending(x => x.TotalDispatched)
            .ToListAsync();

        return Ok(summary);
    }

    // POST /api/vehicledispatches/sync/today
    [HttpPost("sync/today")]
    public async Task<IActionResult> SyncToday()
        => Ok(await _sync.SyncVehicleDispatchesForDateAsync(DateTime.UtcNow.Date));

    // POST /api/vehicledispatches/sync/date/2026-05-28
    [HttpPost("sync/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd format." });
        return Ok(await _sync.SyncVehicleDispatchesForDateAsync(parsed));
    }

    // POST /api/vehicledispatches/sync/range
    [HttpPost("sync/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To, out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd for both dates." });

        var result = await _sync.SyncVehicleDispatchesForRangeAsync(from, to);
        return Ok(result);
    }

    [HttpPost("backfill")]
    public IActionResult StartBackfill([FromQuery] bool forceResync = false)
    {
        _ = Task.Run(() => _sync.BackfillVehicleDispatchesAsync(forceResync: forceResync));
        return Accepted(new { message = $"Full VDR backfill started (range-based, forceResync={forceResync})." });
    }
}


