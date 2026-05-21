using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    private readonly DataSyncService _sync;
    private readonly AppDbContext _db;
    private readonly ILogger<SyncController> _logger;

    public SyncController(DataSyncService sync, AppDbContext db, ILogger<SyncController> logger)
    {
        _sync = sync;
        _db = db;
        _logger = logger;
    }

    // ── GET /api/sync/status ─────────────────────────────────
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var latestLogs = await _db.DmsSyncLogs
            .OrderByDescending(l => l.StartedAt)
            .Take(20)
            .ToListAsync();

        var totalServiceRecords = await _db.DmsServiceHistories.CountAsync();
        var totalDealers = await _db.DmsDealers.CountAsync();

        var tokenActive = await _db.DmsAuthTokens
            .Where(t => t.IsActive && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.ExpiresAt, t.VendorName })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            TotalServiceRecords = totalServiceRecords,
            TotalDealers        = totalDealers,
            ActiveToken         = tokenActive,
            RecentSyncLogs      = latestLogs
        });
    }

    // ── POST /api/sync/dealers ───────────────────────────────
    [HttpPost("dealers")]
    public async Task<IActionResult> SyncDealers()
    {
        _logger.LogInformation("Manual dealer sync triggered");
        var result = await _sync.SyncAllDealersAsync();
        return Ok(result);
    }

    // ── POST /api/sync/service-history/today ────────────────
    [HttpPost("service-history/today")]
    public async Task<IActionResult> SyncToday()
    {
        var result = await _sync.SyncServiceHistoryForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    // ── POST /api/sync/service-history/date/{date} ──────────
    /// <summary>date format: yyyy-MM-dd e.g. 2022-07-27</summary>
    [HttpPost("service-history/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Invalid date. Use yyyy-MM-dd format." });

        var result = await _sync.SyncServiceHistoryForDateAsync(parsed);
        return Ok(result);
    }

    // ── POST /api/sync/service-history/range ────────────────
    [HttpPost("service-history/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To, out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd format for both dates." });

        if ((to - from).TotalDays > 365)
            return BadRequest(new { error = "Max range is 365 days per request." });

        // Fire and forget for large ranges
        _ = Task.Run(() => _sync.BackfillHistoricalDataAsync(from, to));
        return Accepted(new
        {
            message = $"Backfill started for {from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            from = from.ToString("dd-MM-yyyy"),
            to = to.ToString("dd-MM-yyyy")
        });
    }

    // ── POST /api/sync/backfill ──────────────────────────────
    /// <summary>Runs full historical backfill from configured start date to today</summary>
    [HttpPost("backfill")]
    public IActionResult StartBackfill()
    {
        _ = Task.Run(() => _sync.BackfillHistoricalDataAsync());
        return Accepted(new { message = "Full historical backfill started. Check /api/sync/status." });
    }

    // ── POST /api/sync/refresh-token ────────────────────────
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromServices] ErpApiService erpApi)
    {
        try
        {
            var token = await erpApi.GetValidTokenAsync();
            return Ok(new { message = "Token refreshed", tokenPreview = token[..20] + "..." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

