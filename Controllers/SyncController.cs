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
            .AsNoTracking()
            .ToListAsync();

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

    private async Task<long> GetApproxCountAsync(string tableName)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 10;
        cmd.CommandText = @"
            SELECT SUM(p.rows)
            FROM sys.partitions p
            WHERE p.object_id = OBJECT_ID(@tableName)
            AND p.index_id IN (0, 1);";

        var param = cmd.CreateParameter();
        param.ParameterName = "@tableName";
        param.Value = tableName;
        cmd.Parameters.Add(param);

        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value || result == null ? 0 : Convert.ToInt64(result);
    }

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

    [HttpPost("dealers")]
    public async Task<IActionResult> SyncDealers()
    {
        _logger.LogInformation("Manual dealer sync triggered");
        var result = await _sync.SyncAllDealersAsync();
        return Ok(result);
    }

    [HttpPost("service-history/today")]
    public async Task<IActionResult> SyncToday()
    {
        var result = await _sync.SyncServiceHistoryForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    [HttpPost("service-history/force-today")]
    public async Task<IActionResult> ForceSyncToday()
    {
        _logger.LogInformation("Force sync triggered for today: {date}", DateTime.UtcNow.Date);
        var result = await _sync.SyncServiceHistoryForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    [HttpPost("service-history/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Invalid date. Use yyyy-MM-dd format." });

        var result = await _sync.SyncServiceHistoryForDateAsync(parsed);
        return Ok(result);
    }

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

    [HttpPost("service-history/debug-raw/{date}")]
    public async Task<IActionResult> DebugRaw(string date, [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd" });

        var token   = await erpApi.GetValidTokenAsync();
        var rawJson = await erpApi.FetchRawDjrAsync(parsed, token);

        return Ok(new
        {
            Length       = rawJson.Length,
            RawPreview   = rawJson[..Math.Min(3000, rawJson.Length)]
        });
    }

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

    [HttpPost("service-history/debug-raw-range")]
    public async Task<IActionResult> DebugRawDjrRange(
        [FromQuery] DateTime from, [FromQuery] DateTime to,
        [FromServices] ErpApiService erpApi)
    {
        var token = await erpApi.GetValidTokenAsync();
        var raw   = await erpApi.FetchRawDjrRangeAsync(from, to, token);
        return Ok(new { Length = raw.Length, Preview = raw[..Math.Min(3000, raw.Length)] });
    }

    // ─────────────────────────────────────────────────────────
    // POST /api/sync/service-history/trace-count?from=2021-01-01&to=2021-01-31
    //
    // FIX: updated to use the SAME 4-part composite key as
    // DataSyncService — DealerCode + JobNo + JobDate + ChassisNo —
    // instead of the old DealerCode|JobNo pair. Previously this trace
    // endpoint reported dedup/insert-vs-update numbers based on a key
    // that no longer matches what the real sync path actually uses,
    // so its "duplicate" and "would insert/update" figures were
    // misleading once DataSyncService moved to the 4-part UniqueKey.
    //
    // Full pipeline trace for DJR — shows exactly where record count
    // changes between the raw ERP response and what actually lands in
    // DMS_ServiceHistory, without doing any inserts/updates (read-only).
    // Use this to pinpoint whether loss happens at:
    //   1) ERP raw fetch (network/timeout/ERP-side truncation)
    //   2) JSON parse/sanitize (malformed records dropped)
    //   3) Dedup (JobNo == "Total" rows, blank DealerCode/JobNo, or
    //      duplicate DealerCode+JobNo+JobDate+ChassisNo combinations
    //      collapsing multiple rows within the same fetched batch)
    //   4) DB save (would-be inserts, not actually executed here —
    //      "update" no longer applies since UniqueKey duplicates are
    //      skipped, not merged)
    // ─────────────────────────────────────────────────────────
    [HttpPost("service-history/trace-count")]
    public async Task<IActionResult> TraceServiceHistoryCount(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromServices] ErpApiService erpApi,
        [FromServices] AppDbContext db)
    {
        var token = await erpApi.GetValidTokenAsync();

        // Stage 1: raw response length, before any parsing
        var raw = await erpApi.FetchRawDjrRangeAsync(from, to, token);
        var rawLength = raw.Length;

        // Stage 2: parsed count via the SAME code path production uses
        List<AutoGeniusSync.DTOs.DjrValue> parsed;
        string? parseError = null;
        try
        {
            parsed = await erpApi.FetchDjrRangeAsync(from, to, token);
        }
        catch (Exception ex)
        {
            parsed = new();
            parseError = ex.Message;
        }

        var totalParsed = parsed.Count;

        // Stage 3a: drop the ERP's own "Total" summary rows
        var afterTotalFilter = parsed.Where(j => j.JobNo != "Total").ToList();
        var droppedAsTotal = totalParsed - afterTotalFilter.Count;

        // Stage 3b: drop rows missing any part of the composite key.
        // FIX: JobDate is now part of the key, so a record missing a
        // parseable JobDate can't be uniquely identified either — it's
        // now included in the "blank key" drop count, same as a blank
        // DealerCode or JobNo would be. ChassisNo is checked too, since
        // it's the 4th part of the key.
        static DateOnly? ResolveJobDate(DjrValue j) =>
            DateOnly.TryParse(j.JobDate, out var d) ? d : (DateOnly?)null;

        var afterBlankFilter = afterTotalFilter
            .Where(j => !string.IsNullOrEmpty(j.DealerCode)
                     && !string.IsNullOrEmpty(j.JobNo)
                     && !string.IsNullOrEmpty(j.ChassisNo)
                     && ResolveJobDate(j) != null)
            .ToList();
        var droppedAsBlank = afterTotalFilter.Count - afterBlankFilter.Count;

        // Stage 3c: dedup by the SAME 4-part key DataSyncService uses —
        // DealerCode|JobNo|JobDate|ChassisNo — last-one-wins within batch.
        var groupedByKey = afterBlankFilter
            .GroupBy(j => BuildTraceKey(j.DealerCode, j.JobNo, ResolveJobDate(j), j.ChassisNo))
            .ToList();

        var dedupedJobs = groupedByKey.Select(g => g.Last()).ToList();
        var droppedAsDuplicateKey = afterBlankFilter.Count - dedupedJobs.Count;

        // Stage 4: how many of these composite keys ALREADY exist in DB.
        // FIX: no more "would update" — an existing key means the record
        // is an exact-combination duplicate and gets SKIPPED, not merged.
        // Only genuinely new combinations count toward "would insert."
        var candidateKeys = groupedByKey
            .Select(g => BuildTraceKey(
                dedupedJobs.First(j => BuildTraceKey(j.DealerCode, j.JobNo, ResolveJobDate(j), j.ChassisNo) == g.Key).DealerCode,
                dedupedJobs.First(j => BuildTraceKey(j.DealerCode, j.JobNo, ResolveJobDate(j), j.ChassisNo) == g.Key).JobNo,
                ResolveJobDate(dedupedJobs.First(j => BuildTraceKey(j.DealerCode, j.JobNo, ResolveJobDate(j), j.ChassisNo) == g.Key)),
                dedupedJobs.First(j => BuildTraceKey(j.DealerCode, j.JobNo, ResolveJobDate(j), j.ChassisNo) == g.Key).ChassisNo))
            .ToHashSet();

        var existingKeys = await db.DmsServiceHistories
            .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync();

        var alreadyInDb = existingKeys.Distinct().Count();
        var wouldInsert = dedupedJobs.Count - alreadyInDb;

        var duplicateKeySamples = groupedByKey
            .Where(g => g.Count() > 1)
            .Take(10)
            .Select(g => new
            {
                Key = g.Key,
                CountInResponse = g.Count(),
                JobDates = g.Select(x => x.JobDate).Distinct()
            });

        return Ok(new
        {
            DateRange = new { From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd") },

            Stage1_RawResponse = new { RawLength = rawLength },

            Stage2_Parsed = new { TotalParsedFromErp = totalParsed, ParseError = parseError },

            Stage3_Filtering = new
            {
                TotalParsed             = totalParsed,
                DroppedAsRowTotal       = droppedAsTotal,
                AfterRowTotalFilter     = afterTotalFilter.Count,
                DroppedAsBlankKey       = droppedAsBlank,
                AfterBlankKeyFilter     = afterBlankFilter.Count,
                DroppedAsDuplicateKey   = droppedAsDuplicateKey,
                FinalDedupedCount       = dedupedJobs.Count
            },

            Stage4_DbComparison = new
            {
                AlreadyInDbAsExactDuplicate = alreadyInDb,
                WouldInsert                 = wouldInsert,
                NetNewRecordsForDb          = wouldInsert
            },

            DuplicateKeySamples = duplicateKeySamples
        });
    }

    // Mirrors DataSyncService.BuildUniqueKey exactly — keep these in
    // sync if the key definition ever changes again.
    private static string BuildTraceKey(string? dealerCode, string? jobNo, DateOnly? date, string? chassisNo)
    => $"{dealerCode?.Trim().ToUpperInvariant()}{jobNo?.Trim().ToUpperInvariant()}{date?.ToString("yyyy-MM-dd")}{chassisNo?.Trim().ToUpperInvariant()}";
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

    [HttpPost("dealers/bapl")]
    public async Task<IActionResult> SyncDealersFromBapl()
    {
        var result = await _sync.SyncDealersFromBaplAsync();
        return Ok(result);
    }

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

    [HttpPost("lor/backfill")]
    public async Task<IActionResult> TriggerLorBackfill(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        _logger.LogInformation("Manual LOR backfill {from} → {to}", from, to);
        _ = Task.Run(() => _sync.BackfillLineOrderReportAsync(from, to));
        return Accepted(new
        {
            message = $"LOR backfill started: {from:yyyy-MM-dd} → {to:yyyy-MM-dd}. Check /api/sync/status."
        });
    }

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