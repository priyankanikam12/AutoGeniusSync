using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;

namespace AutoGeniusSync.Controllers;

// Manual / scheduled trigger for the nightly full reload.
//
// WHY THIS EXISTS: under IIS the app (and the timer inside SyncHostedService) only runs while the
// app pool is alive. A Windows Task Scheduler job that POSTs to /start at 00:00 wakes the app AND
// starts the reload, so a stopped/idle app pool can no longer make the midnight run silently
// disappear. It also lets you re-run the reload by hand after a deploy (RunOnStartup is false).
//
// Both endpoints require header X-API-Key = appsettings "ApiKeys:Internal".
// Nothing here truncates any more: mode=full re-checks every month from HistoricalStartDate with
// UPSERT (checkpoints reset) — still guarded by ?confirm=yes because it is a long, heavy run.
// mode=historical resumes the month-wise / dealer-wise historical load from DMS_SyncCheckpoint.
[ApiController]
[Route("api/admin/reload")]
public class NightlyReloadController : ControllerBase
{
    private readonly NightlyReloadRunner _runner;
    private readonly IConfiguration _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceScopeFactory _scopeFactory;

    public NightlyReloadController(NightlyReloadRunner runner, IConfiguration config, IHostApplicationLifetime lifetime,
                                   IServiceScopeFactory scopeFactory)
    {
        _runner = runner;
        _config = config;
        _lifetime = lifetime;
        _scopeFactory = scopeFactory;
    }

    // Result of the last ERP probe (in memory only).
    private sealed record ProbeState(bool Running, DateTime? StartedUtc, DateTime? FinishedUtc,
                                     List<DataSyncService.ProbeRow>? Rows, string? Error);
    private static ProbeState? _probe;
    private static readonly object _probeLock = new();

    private bool IsValidApiKey(string? apiKey)
    {
        var expected = _config["ApiKeys:Internal"];
        return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
    }

    // POST /api/admin/reload/start?mode=window|full|missing|historical&confirm=yes
    //   mode=historical → resume the checkpoint sync (HistoricalStartDate → today, month-wise, dealer-wise,
    //                     completed months skipped). Optional &tables=djr,vsr,vdr,lor.
    //   mode omitted → Sync:Mode from appsettings (Window by default).
    //   mode=window  → last Sync:RollingMonths months, upsert, NO truncate (no confirm needed).
    //   mode=full    → TRUNCATE the 4 tables + reload from SyncSettings:HistoricalStartDate (needs confirm=yes).
    //   mode=missing → NO truncate. Loads only the tables that are EMPTY now (insert-only), so tables that
    //                  already hold data are untouched. Optional: &tables=vsr,vdr,lor,djr (force those tables,
    //                  existing rows are skipped), &from=yyyy-MM-dd&to=yyyy-MM-dd (limit the range, e.g. to repair
    //                  one failed month: mode=missing&tables=djr&from=2025-02-01&to=2025-02-28).
    [HttpPost("start")]
    public IActionResult Start(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] string? confirm, [FromQuery] string? mode,
        [FromQuery] string? tables, [FromQuery] string? from, [FromQuery] string? to)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        if (string.Equals(mode, "fyrefresh", StringComparison.OrdinalIgnoreCase))
        {
            if (_runner.IsRunning)
                return Conflict(new { error = "A sync is already running.", status = _runner.GetStatus() });
            _ = Task.Run(() => _runner.RunFyRefreshAsync("manual-api", _lifetime.ApplicationStopping));
            return Accepted(new
            {
                mode = "FyRefresh",
                message = "Current-FY refresh requested (1 April → yesterday into staging; each table replaced in one transaction only if " +
                          "its fetch is complete). Poll GET api/admin/reload/fy."
            });
        }

        if (string.Equals(mode, "historical", StringComparison.OrdinalIgnoreCase))
        {
            string[]? apis = null;
            if (!string.IsNullOrWhiteSpace(tables))
            {
                var parts = tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var bad = parts.Where(t => AutoGeniusSync.Models.SyncApi.Normalize(t) == null).ToArray();
                if (bad.Length > 0)
                    return BadRequest(new { error = $"Unknown table(s): {string.Join(", ", bad)}. Use djr, vsr, vdr, lor." });
                apis = parts.Select(t => AutoGeniusSync.Models.SyncApi.Normalize(t)!).Distinct().ToArray();
            }
            if (_runner.IsRunning)
                return Conflict(new { error = "A sync is already running.", status = _runner.GetStatus() });

            _ = Task.Run(() => _runner.RunHistoricalAsync("manual-api", _lifetime.ApplicationStopping, null, apis));
            return Accepted(new
            {
                mode = "Historical",
                apis = apis ?? AutoGeniusSync.Models.SyncApi.All,
                message = "Historical checkpoint sync requested (resumes from the last completed month). " +
                          "Poll GET api/admin/reload/status and GET api/admin/reload/checkpoints."
            });
        }

        if (string.Equals(mode, "missing", StringComparison.OrdinalIgnoreCase))
        {
            string[]? tableList = null;
            if (!string.IsNullOrWhiteSpace(tables))
            {
                tableList = tables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(t => t.ToLowerInvariant()).Distinct().ToArray();
                var bad = tableList.Where(t => t is not ("djr" or "vsr" or "vdr" or "lor")).ToArray();
                if (bad.Length > 0)
                    return BadRequest(new { error = $"Unknown table(s): {string.Join(", ", bad)}. Use djr, vsr, vdr, lor." });
            }

            DateTime? fromDate = null, toDate = null;
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateTime.TryParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var f))
                    return BadRequest(new { error = "from must be yyyy-MM-dd." });
                fromDate = f;
            }
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateTime.TryParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t))
                    return BadRequest(new { error = "to must be yyyy-MM-dd." });
                toDate = t;
            }
            if (fromDate.HasValue && toDate.HasValue && toDate < fromDate)
                return BadRequest(new { error = "to is before from." });

            if (_runner.IsRunning)
                return Conflict(new { error = "A sync is already running.", status = _runner.GetStatus() });

            var req = new MissingRequest(tableList, fromDate, toDate);
            _ = Task.Run(() => _runner.RunAsync("manual-api", _lifetime.ApplicationStopping, null, req));

            return Accepted(new
            {
                mode = "Missing",
                tables = tableList ?? new[] { "(every table that is empty now)" },
                from = fromDate?.ToString("yyyy-MM-dd") ?? "SyncSettings:HistoricalStartDate",
                to = toDate?.ToString("yyyy-MM-dd") ?? "today (IST)",
                message = "Missing-tables load requested (no truncate, insert-only). Poll GET api/admin/reload/status."
            });
        }

        bool full;
        if (string.IsNullOrWhiteSpace(mode))
            full = NightlyReloadRunner.IsFullMode(_config["Sync:Mode"]);
        else if (string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            full = true;
        else if (string.Equals(mode, "window", StringComparison.OrdinalIgnoreCase))
            full = false;
        else
            return BadRequest(new { error = "mode must be 'window', 'full', 'missing', 'historical' or 'fyrefresh'." });

        if (full && !string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                error = "mode=full re-checks EVERY month from SyncSettings:HistoricalStartDate (all checkpoints reset, UPSERT, no truncate). " +
                        "It is a long run. Add ?confirm=yes to proceed, or use mode=historical / mode=window."
            });

        if (_runner.IsRunning)
            return Conflict(new { error = "A sync is already running.", status = _runner.GetStatus() });

        // Fire-and-forget, same pattern as the existing backfill endpoints. RunAsync never throws and is
        // cancelled via ApplicationStopping if the app shuts down.
        _ = Task.Run(() => _runner.RunAsync("manual-api", _lifetime.ApplicationStopping, full));

        return Accepted(new
        {
            mode = full ? "Full" : "Window",
            message = full
                ? "Full re-check requested (ERP check → checkpoints reset → month-wise UPSERT → validate; no truncate). Poll GET api/admin/reload/status."
                : "Window sync requested (rolling months, upsert, no truncate). Poll GET api/admin/reload/status.",
        });
    }

    // POST /api/admin/reload/probe  → starts a READ-ONLY check of the ERP report APIs (VSR, VDR, LOR; DJR as control).
    // GET  /api/admin/reload/probe  → result: row count, milliseconds and error per call. No business data, nothing is written.
    [HttpPost("probe")]
    public IActionResult StartProbe([FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        lock (_probeLock)
        {
            if (_probe is { Running: true })
                return Conflict(new { error = "A probe is already running." });
            _probe = new ProbeState(true, DateTime.UtcNow, null, null, null);
        }

        var started = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc  = scope.ServiceProvider.GetRequiredService<DataSyncService>();
                var rows = await svc.ProbeErpReportsAsync(_lifetime.ApplicationStopping);
                lock (_probeLock) _probe = new ProbeState(false, started, DateTime.UtcNow, rows, null);
            }
            catch (Exception ex)
            {
                lock (_probeLock) _probe = new ProbeState(false, started, DateTime.UtcNow, null, ex.InnerException?.Message ?? ex.Message);
            }
        });

        return Accepted(new { message = "ERP probe started (read-only). Poll GET api/admin/reload/probe." });
    }

    [HttpGet("probe")]
    public IActionResult GetProbe([FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        lock (_probeLock)
            return _probe == null ? NotFound(new { error = "No probe has been run yet. POST api/admin/reload/probe first." }) : Ok(_probe);
    }

    // GET /api/admin/reload/checkpoints — progress of the month-wise / dealer-wise sync:
    // periods per API and status, the month each API resumes from, and the latest failures.
    [HttpGet("checkpoints")]
    public async Task<IActionResult> Checkpoints([FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
        return Ok(await svc.GetCheckpointSummaryAsync(HttpContext.RequestAborted));
    }

    // POST /api/admin/reload/checkpoints/reset?table=djr&from=2025-01-01&to=2025-03-31&dealerCode=X&failedOnly=true&confirm=yes
    // Puts the selected months back to Pending so the next historical run fetches them again (UPSERT —
    // nothing is deleted). Use it after a manual truncate or to force a re-check of a range.
    [HttpPost("checkpoints/reset")]
    public async Task<IActionResult> ResetCheckpoints(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] string? table, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? dealerCode, [FromQuery] bool failedOnly = false, [FromQuery] string? confirm = null)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });
        if (!string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Add ?confirm=yes. The selected months will be fetched again (UPSERT, nothing deleted)." });

        string? api = null;
        if (!string.IsNullOrWhiteSpace(table))
        {
            api = AutoGeniusSync.Models.SyncApi.Normalize(table);
            if (api == null) return BadRequest(new { error = "table must be djr, vsr, vdr or lor." });
        }
        DateTime? f = null, t = null;
        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!DateTime.TryParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var fv))
                return BadRequest(new { error = "from must be yyyy-MM-dd." });
            f = fv;
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!DateTime.TryParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var tv))
                return BadRequest(new { error = "to must be yyyy-MM-dd." });
            t = tv;
        }

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
        var n = await svc.ResetCheckpointsAsync(api, f, t, dealerCode, failedOnly, HttpContext.RequestAborted);
        return Ok(new { reset = n, message = $"{n} period(s) set to Pending. They are fetched on the next historical run." });
    }

    // GET /api/admin/reload/fy — last current-FY refresh runs and unit progress per table.
    [HttpGet("fy")]
    public async Task<IActionResult> FyStatus([FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
        return Ok(await svc.GetFyRefreshStatusAsync(HttpContext.RequestAborted));
    }

    // GET /api/admin/reload/status
    [HttpGet("status")]
    public IActionResult Status([FromHeader(Name = "X-API-Key")] string? apiKey)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        return Ok(_runner.GetStatus());
    }
}