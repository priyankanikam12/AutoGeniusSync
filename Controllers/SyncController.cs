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
    // Controllers/SyncController.cs — replace GetStatus with this
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var latestLogs = await _db.DmsSyncLogs
            .OrderByDescending(l => l.StartedAt)
            .Take(20)
            .AsNoTracking()
            .ToListAsync();

        // NOLOCK via raw SQL — dashboard counts don't need to block on
        // or be blocked by in-flight backfill writes to these tables.
        var totalServiceRecords = await GetApproxCountAsync("dbo.DMS_ServiceHistory");
        var totalDealers        = await GetApproxCountAsync("dbo.DMS_Dealers");

        var tokenActive = await _db.DmsAuthTokens
            .Where(t => t.IsActive && t.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.ExpiresAt, t.VendorName })
            .AsNoTracking()
            .FirstOrDefaultAsync();

        return Ok(new
        {
            TotalServiceRecords = totalServiceRecords,
            TotalDealers        = totalDealers,
            ActiveToken         = tokenActive,
            RecentSyncLogs      = latestLogs
        });
    }

    // Fast, non-blocking row count using sys.dm_db_partition_stats.
    // This reads SQL Server's internal metadata (updated continuously by
    // the engine) instead of scanning/locking the actual table — it never
    // waits behind other transactions and returns in milliseconds even on
    // huge tables under heavy write load.
    private async Task<long> GetApproxCountAsync(string tableName)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 10; // this must return fast — if it doesn't, something else is wrong
        cmd.CommandText = @"
            SELECT SUM(p.rows)
            FROM sys.partitions p
            WHERE p.object_id = OBJECT_ID(@tableName)
            AND p.index_id IN (0, 1);"; // 0 = heap, 1 = clustered index

        var param = cmd.CreateParameter();
        param.ParameterName = "@tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value || result == null ? 0 : Convert.ToInt64(result);
    }

    // ── POST /api/sync/service-history/debug-raw-djrn/{date} ───
    /// <summary>
    /// Returns raw DJRN JSON before deserialization, so we can confirm
    /// whether its field names actually match DjrValue or need their own DTO.
    /// date format: yyyy-MM-dd
    /// </summary>
    [HttpPost("service-history/debug-raw-djrn/{date}")]
    public async Task<IActionResult> DebugRawDjrn(
        string date,
        [FromServices] ErpApiService erpApi,
        [FromQuery] string dealerCode = "",
        [FromQuery] string chassisNo = "")
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd" });

        var token   = await erpApi.GetValidTokenAsync();
        var rawJson = await erpApi.FetchRawDjrnAsync(parsed, token, chassisNo, dealerCode);

        return Ok(new
        {
            Length     = rawJson.Length,
            RawPreview = rawJson[..Math.Min(3000, rawJson.Length)]
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

    // POST /api/sync/service-history/debug-parse-range?from=2020-09-12&to=2021-09-12
    // Surfaces the EXACT JSON parse failure (if any) for a DJR range —
    // this is what DeserializeTolerant silently swallows in the real sync path.
    [HttpPost("service-history/debug-parse-range")]
    public async Task<IActionResult> DebugParseRangeDjr([FromQuery] DateTime from, [FromQuery] DateTime to, [FromServices] ErpApiService erpApi)
    {
        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawDjrRangeAsync(from, to, token);
        return Ok(erpApi.DebugParseRawJson<DjrValue>(raw));
    }

    [HttpPost("vsr/debug-parse-range")]
    public async Task<IActionResult> DebugParseRangeVsr([FromQuery] DateTime from, [FromQuery] DateTime to, [FromServices] ErpApiService erpApi)
    {
        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawVsrRangeAsync(from, to, token);
        return Ok(erpApi.DebugParseRawJson<VsrValue>(raw));
    }

    [HttpPost("vdr/debug-parse-range")]
    public async Task<IActionResult> DebugParseRangeVdr([FromQuery] DateTime from, [FromQuery] DateTime to, [FromServices] ErpApiService erpApi)
    {
        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawVdrRangeAsync(from, to, token);
        return Ok(erpApi.DebugParseRawJson<VdrValue>(raw));
    }

    // ── POST /api/sync/service-history/range ────────────────
    [HttpPost("service-history/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To,   out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd format for both dates." });

        var result = await _sync.SyncServiceHistoryForRangeAsync(from, to);
        return Ok(result);
    }

    [HttpPost("backfill")]
    public IActionResult StartBackfill([FromQuery] bool forceResync = false)
    {
        _ = Task.Run(() => _sync.BackfillHistoricalDataAsync(forceResync: forceResync));
        return Accepted(new { message = $"Full historical backfill started (range-based, forceResync={forceResync}). Check /api/sync/status." });
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

    // POST /api/sync/lor/{date}
    // Example: POST /api/sync/lor/2025-06-01
    [HttpPost("lor/{date}")]
    public async Task<IActionResult> TriggerLorSync(string date)
    {
        if (!DateTime.TryParse(date, out var parsedDate))
            return BadRequest("Invalid date format. Use YYYY-MM-DD");

        _logger.LogInformation("Manual LOR sync triggered for {date}", date);
        var result = await _sync.SyncLineOrderReportForDateAsync(parsedDate);

        return Ok(new
        {
            result.SyncType,
            result.RecordsFetched,
            result.RecordsInserted,
            result.RecordsUpdated,
            result.Error
        });
    }

    // POST /api/sync/service-history/debug-raw-range?from=2020-09-12&to=2021-09-12
    [HttpPost("service-history/debug-raw-range")]
    public async Task<IActionResult> DebugRawDjrRange(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromServices] ErpApiService erpApi)
    {
        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawDjrRangeAsync(from, to, token);
        return Ok(new { Length = raw.Length, Preview = raw[..Math.Min(3000, raw.Length)] });
    }

    // ── POST /api/sync/shadowfax/realtime ────────────────────
    // Manually trigger the fast, restricted Shadowfax-only LOR sync
    // (only the dealer codes in ShadowfaxSettings:DealerCodes,
    // over the last ShadowfaxSettings:LookbackDays days). This also
    // runs automatically every ShadowfaxSettings:RealtimeIntervalMinutes.
    [HttpPost("shadowfax/realtime")]
    public async Task<IActionResult> TriggerShadowfaxRealtime()
    {
        _logger.LogInformation("Manual Shadowfax realtime sync triggered");
        var result = await _sync.SyncShadowfaxRealtimeAsync();
        return Ok(new
        {
            result.SyncType,
            result.RecordsFetched,
            result.RecordsInserted,
            result.RecordsUpdated,
            result.Error
        });
    }

    // ── POST /api/sync/reconcile?lookbackDays=90 ─────────────
    // Manually trigger the open-jobs reconciliation sweep.
    // Use a large lookbackDays (e.g. 730) once, right after deploying
    // the index fix, to catch historical open jobs that already
    // closed on the ERP side but never got refreshed locally.
    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile([FromQuery] int lookbackDays = 90)
    {
        _logger.LogInformation("Manual reconcile triggered, lookback {d} days", lookbackDays);
        var result = await _sync.ReconcileOpenJobsAsync(lookbackDays);
        return Ok(new
        {
            result.SyncType,
            result.RecordsFetched,
            result.RecordsInserted,
            result.RecordsUpdated,
            result.Error
        });
    }

    // POST /api/sync/lor/backfill?from=2022-01-01&to=2026-06-09
    [HttpPost("lor/backfill")]
    public async Task<IActionResult> TriggerLorBackfill(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        _logger.LogInformation("Manual LOR backfill {from} → {to}", from, to);
        // Run in background — this can take a while across many dealers
        _ = Task.Run(() => _sync.BackfillLineOrderReportAsync(from, to));
        return Accepted(new
        {
            message = $"LOR backfill started: {from:yyyy-MM-dd} → {to:yyyy-MM-dd}. Check /api/sync/status."
        });
    }

    // POST /api/sync/lor/range?from=2026-01-01&to=2026-06-09
    [HttpPost("lor/range")]
    public async Task<IActionResult> TriggerLorRange(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to)
    {
        var result = await _sync.SyncLineOrderReportAsync(from, to);
        return Ok(new
        {
            result.SyncType,
            result.RecordsFetched,
            result.RecordsInserted,
            result.RecordsUpdated,
            result.Error
        });
    }

    // POST /api/sync/lor/debug?dealerCode=CUS0087&date=2026-04-01
    [HttpPost("lor/debug")]
    public async Task<IActionResult> DebugLor(
        [FromQuery] string dealerCode,
        [FromQuery] string date,
        [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest("Use yyyy-MM-dd");

        var token   = await erpApi.GetValidTokenAsync();
        var records = await erpApi.FetchLorAsync(dealerCode, parsed, parsed, token);

        return Ok(new
        {
            DealerCode    = dealerCode,
            Date          = date,
            TotalFetched  = records.Count,
            Sample        = records.Take(3).Select(r => new
            {
                r.UniqueId,
                r.DealerCode,
                r.JobNo,
                r.JobDate,
                r.ChassisNo,
                r.JobCardType,
                r.ItemType,
                r.TotalAmount
            })
        });
    }

    // In SyncController.cs — add this endpoint
    [HttpPost("lor/debug-raw")]
    public async Task<IActionResult> DebugLorRaw(
        [FromQuery] string dealerCode,
        [FromQuery] string date,
        [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest("Use yyyy-MM-dd");

        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawLorAsync(dealerCode, parsed, token);

        return Ok(new
        {
            Length     = raw.Length,
            Preview    = raw[..Math.Min(2000, raw.Length)]
        });
    }
}