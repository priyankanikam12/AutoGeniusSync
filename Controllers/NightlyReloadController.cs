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

    // POST /api/admin/reload/start?confirm=yes
    [HttpPost("start")]
    public IActionResult Start([FromHeader(Name = "X-API-Key")] string? apiKey, [FromQuery] string? confirm)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        if (!string.Equals(confirm, "yes", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new
            {
                error = "This TRUNCATES ServiceHistory, VehicleSales, VehicleDispatches and LineOrderReport and re-fetches " +
                        "everything from SyncSettings:HistoricalStartDate. Add ?confirm=yes to proceed."
            });

        if (_runner.IsRunning)
            return Conflict(new { error = "A reload is already running.", status = _runner.GetStatus() });

        // Fire-and-forget, same pattern as the existing backfill endpoints. RunAsync never throws and is
        // cancelled via ApplicationStopping if the app shuts down.
        _ = Task.Run(() => _runner.RunAsync("manual-api", _lifetime.ApplicationStopping));

        return Accepted(new
        {
            message = "Full reload requested (ERP check → truncate → re-fetch → validate). Poll GET api/admin/reload/status.",
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