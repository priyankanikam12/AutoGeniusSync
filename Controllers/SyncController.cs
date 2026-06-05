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
        var totalDealers        = await _db.DmsDealers.CountAsync();

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

    // ── POST /api/sync/service-history/force-today ──────────
    [HttpPost("service-history/force-today")]
    public async Task<IActionResult> ForceSyncToday()
    {
        _logger.LogInformation("Force sync triggered for today: {date}", DateTime.UtcNow.Date);
        var result = await _sync.SyncServiceHistoryForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    // ── POST /api/sync/service-history/date/{date} ──────────
    /// <summary>date format: yyyy-MM-dd e.g. 2024-06-01</summary>
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
            !DateTime.TryParse(req.To,   out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd format for both dates." });

        if ((to - from).TotalDays > 365)
            return BadRequest(new { error = "Max range is 365 days per request." });

        _ = Task.Run(() => _sync.BackfillHistoricalDataAsync(from, to));
        return Accepted(new
        {
            message = $"Backfill started for {from:dd-MM-yyyy} to {to:dd-MM-yyyy}",
            from    = from.ToString("dd-MM-yyyy"),
            to      = to.ToString("dd-MM-yyyy")
        });
    }

    // ── POST /api/sync/backfill ──────────────────────────────
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

    // ── POST /api/sync/service-history/debug/{date} ─────────
    /// <summary>
    /// Shows parsed field values after deserialization.
    /// Use this to confirm DealerCode/JobNo are now populated after the SanitizeJson fix.
    /// date format: yyyy-MM-dd
    /// </summary>
    [HttpPost("service-history/debug/{date}")]
    public async Task<IActionResult> DebugDate(string date, [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd" });

        var token = await erpApi.GetValidTokenAsync();
        var jobs  = await erpApi.FetchDjrAsync(parsed, token, "");

        var emptyDealerCount = jobs.Count(j => string.IsNullOrEmpty(j.DealerCode));
        var emptyJobNoCount  = jobs.Count(j => string.IsNullOrEmpty(j.JobNo));
        var bothEmptyCount   = jobs.Count(j => string.IsNullOrEmpty(j.DealerCode) && string.IsNullOrEmpty(j.JobNo));

        // Show first 5 records with key fields so you can confirm mapping is working
        var sample = jobs.Take(5).Select(j => new
        {
            j.DealerCode,
            j.JobNo,
            j.JobDate,
            j.ChassisNo,
            j.Model,
            j.NetTotal
        });

        return Ok(new
        {
            TotalFetched    = jobs.Count,
            EmptyDealerCode = emptyDealerCount,
            EmptyJobNo      = emptyJobNoCount,
            BothEmpty       = bothEmptyCount,
            Sample          = sample
        });
    }

    // ── POST /api/sync/service-history/debug-raw/{date} ─────
    /// <summary>
    /// Returns the raw JSON string from the ERP API before any deserialization.
    /// Use this if debug/{date} still shows empty fields — paste the output
    /// here to see the actual property names the API is sending.
    /// date format: yyyy-MM-dd
    /// </summary>
    [HttpPost("service-history/debug-raw/{date}")]
    public async Task<IActionResult> DebugRaw(string date, [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd" });

        var token   = await erpApi.GetValidTokenAsync();
        var rawJson = await erpApi.FetchRawDjrAsync(parsed, token);

        // Return first 3000 chars — enough to see all property names in one record
        return Ok(new
        {
            Length       = rawJson.Length,
            RawPreview   = rawJson[..Math.Min(3000, rawJson.Length)]
        });
    }
}