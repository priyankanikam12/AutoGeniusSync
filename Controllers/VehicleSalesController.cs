using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VehicleSalesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataSyncService _sync;

    public VehicleSalesController(AppDbContext db, DataSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    // ── GET /api/vehiclesales ────────────────────────────────
    // Query params: from, to, dealerCode, chassisNo, page, pageSize
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? dealerCode,
        [FromQuery] string? chassisNo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsVehicleSales.AsQueryable();

        if (!string.IsNullOrEmpty(dealerCode))
            q = q.Where(v => v.DealerCode == dealerCode);

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(v => v.ChassisNo != null && v.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(v => v.InvoiceDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(v => v.InvoiceDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(v => v.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ── GET /api/vehiclesales/chassis/{chassisNo} ────────────
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
    {
        var records = await _db.DmsVehicleSales
            .Where(v => v.ChassisNo == chassisNo)
            .OrderByDescending(v => v.InvoiceDate)
            .ToListAsync();
        return Ok(records);
    }

    // ── GET /api/vehiclesales/dealer/{dealerCode} ────────────
    [HttpGet("dealer/{dealerCode}")]
    public async Task<IActionResult> GetByDealer(string dealerCode,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleSales.Where(v => v.DealerCode == dealerCode);

        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.InvoiceDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.InvoiceDate <= t);

        return Ok(await q.OrderByDescending(v => v.InvoiceDate).ToListAsync());
    }

    // ── GET /api/vehiclesales/summary ────────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleSales.AsQueryable();

        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.InvoiceDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.InvoiceDate <= t);

        var summary = await q.GroupBy(v => v.DealerCode)
            .Select(g => new
            {
                DealerCode   = g.Key,
                DealerName   = g.Select(x => x.DealerName).FirstOrDefault(),
                TotalSales   = g.Count(),
                TotalRevenue = g.Sum(x => x.NetAmount),
                TotalFameII  = g.Sum(x => x.FameIi)
            })
            .OrderByDescending(x => x.TotalSales)
            .ToListAsync();

        return Ok(summary);
    }

    // ── POST /api/vehiclesales/sync/today ────────────────────
    [HttpPost("sync/today")]
    public async Task<IActionResult> SyncToday()
    {
        var result = await _sync.SyncVehicleSalesForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    // ── POST /api/vehiclesales/sync/date/{date} ──────────────
    [HttpPost("sync/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd format." });

        var result = await _sync.SyncVehicleSalesForDateAsync(parsed);
        return Ok(result);
    }

    // ── POST /api/vehiclesales/sync/range ────────────────────
    [HttpPost("sync/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To, out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd for both dates." });

        if ((to - from).TotalDays > 365)
            return BadRequest(new { error = "Max range is 365 days." });

        _ = Task.Run(() => _sync.BackfillVehicleSalesAsync(from, to));
        return Accepted(new { message = $"VSR backfill started: {from:dd-MM-yyyy} to {to:dd-MM-yyyy}" });
    }

    // ── POST /api/vehiclesales/backfill ──────────────────────
    [HttpPost("backfill")]
    public IActionResult StartBackfill()
    {
        _ = Task.Run(() => _sync.BackfillVehicleSalesAsync());
        return Accepted(new { message = "Full VSR backfill started. Check /api/sync/status." });
    }
}
