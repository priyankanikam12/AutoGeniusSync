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
// /start TRUNCATES DMS_ServiceHistory, DMS_VehicleSales, DMS_VehicleDispatches and
// DMS_LineOrderReport (after an ERP health check) and re-fetches everything — so it also needs
// ?confirm=yes.
[ApiController]
[Route("api/admin/reload")]
public class NightlyReloadController : ControllerBase
{
    private readonly NightlyReloadRunner _runner;
    private readonly IConfiguration _config;
    private readonly IHostApplicationLifetime _lifetime;

    public NightlyReloadController(NightlyReloadRunner runner, IConfiguration config, IHostApplicationLifetime lifetime)
    {
        _runner = runner;
        _config = config;
        _lifetime = lifetime;
    }

    private bool IsValidApiKey(string? apiKey)
    {
        var expected = _config["ApiKeys:Internal"];
        return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
    }

    // POST /api/admin/reload/start?mode=window|full|missing&confirm=yes
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
            return BadRequest(new { error = "mode must be 'window', 'full' or 'missing'." });

        if (full && !string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                error = "mode=full TRUNCATES ServiceHistory, VehicleSales, VehicleDispatches and LineOrderReport and re-fetches " +
                        "everything from SyncSettings:HistoricalStartDate. Add ?confirm=yes to proceed, or use mode=missing / mode=window (no truncate)."
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
                ? "Full reload requested (ERP check → truncate → re-fetch → validate). Poll GET api/admin/reload/status."
                : "Window sync requested (rolling months, upsert, no truncate). Poll GET api/admin/reload/status.",
        });
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