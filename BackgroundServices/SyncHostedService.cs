using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

// Daily scheduler for the FULL reload (ERP check → truncate → re-fetch → validate) of
// ServiceHistory, VehicleSales, VehicleDispatches and LineOrderReport, from
// SyncSettings:HistoricalStartDate up to today (IST) — no from/to dates to enter anywhere.
//
// SELF-HEALING: it does not rely on hitting an exact minute. Every few minutes it asks
// NightlyReloadRunner.CheckCatchUpAsync (database-based): "has today's reload completed, and are all
// 4 tables non-empty?". If today's reload is missing and the scheduled time has passed — or a table
// is empty — it starts the reload automatically, e.g. right after a deploy/restart.
// The reload itself lives in DataSyncService.RunNightlyFullReloadAsync and is started through
// NightlyReloadRunner so the timer and the manual endpoint can never overlap.
//
// IIS NOTE: this timer only fires while the process is alive. In IIS set the app pool to
// Start Mode = AlwaysRunning, Idle Time-out = 0, Regular Time Interval = 0 (and enable
// Preload on the site) — or let a Task Scheduler job call POST api/admin/reload/start at 00:00.
public class SyncHostedService : BackgroundService
{
    private readonly NightlyReloadRunner _runner;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncHostedService> _logger;

    // IST = UTC+5:30. Fixed offset (not TimeZoneInfo lookup) so this
    // works regardless of OS timezone registration.
    private static readonly TimeSpan IstOffset = new TimeSpan(5, 30, 0);

    // Trigger window after the scheduled time. Tolerates a delayed loop tick,
    // but a restart late in the day will NOT fire a second (duplicate) run.
    private static readonly TimeSpan TriggerWindow = TimeSpan.FromMinutes(10);

    // "I am alive" line in the log. A gap in these lines around the scheduled time means the
    // process was not running then (idle/recycled app pool) — that is why a run was missed.
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromHours(1);

    // How often the database check runs, and the first check after start-up (lets the host settle).
    private static readonly TimeSpan CheckEvery     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstCheckWait = TimeSpan.FromSeconds(20);

    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private DateTime _nextCheckUtc     = DateTime.MinValue;

    public SyncHostedService(
        NightlyReloadRunner runner,
        IConfiguration config,
        ILogger<SyncHostedService> logger)
    {
        _runner = runner;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // appsettings: "Sync": { "DailyTimeIst": "00:00", "RunOnStartup": false }
        // DailyTimeIst is HH:mm in IST (24h). "00:00" = midnight IST.
        var timeText = _config["Sync:DailyTimeIst"] ?? "00:00";
        if (!TimeSpan.TryParse(timeText, out var dailyTimeIst)
            || dailyTimeIst < TimeSpan.Zero
            || dailyTimeIst >= TimeSpan.FromDays(1))
        {
            _logger.LogWarning("Invalid Sync:DailyTimeIst '{val}' — using 00:00 IST.", timeText);
            dailyTimeIst = TimeSpan.Zero;
        }

        // WARNING: RunOnStartup=true makes EVERY process start (deploy, IIS app-pool
        // recycle/idle wake-up, server reboot) run the full TRUNCATE + reload — partner APIs
        // return empty/partial data until it finishes. Keep it false in production.
        var runOnStartup = _config.GetValue("Sync:RunOnStartup", false);

        // Catch-up (self-healing) — default ON. Set "Sync:CatchUp": false to use the exact-time window only.
        var catchUp     = _config.GetValue("Sync:CatchUp", true);
        var maxAttempts = Math.Max(1, _config.GetValue("Sync:MaxAttemptsPerDay", 3));

        var startIst = DateTime.UtcNow.Add(IstOffset);
        _logger.LogInformation(
            "SyncHostedService starting at {now} IST — daily FULL reload at {time} IST (history from SyncSettings:HistoricalStartDate to today). " +
            "RunOnStartup={startup}, CatchUp={catchUp}, MaxAttemptsPerDay={max}.",
            startIst.ToString("dd.MM.yyyy HH:mm:ss"),
            dailyTimeIst.ToString(@"hh\:mm"),
            runOnStartup, catchUp, maxAttempts);

        try
        {
            // ── Run once immediately ("start now") ────────────────────────────
            if (runOnStartup)
            {
                var nowIst = DateTime.UtcNow.Add(IstOffset);
                await TriggerAsync("startup", nowIst, ct);
            }

            _nextCheckUtc = DateTime.UtcNow + FirstCheckWait;

            // ── Scheduler loop ────────────────────────────────────────────────
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= _nextCheckUtc)
                    {
                        _nextCheckUtc = DateTime.UtcNow + CheckEvery;
                        var nowIst = DateTime.UtcNow.Add(IstOffset);

                        var decision = await _runner.CheckCatchUpAsync(nowIst, dailyTimeIst, maxAttempts, ct);

                        if (decision.Run && catchUp)
                        {
                            await TriggerAsync(nowIst.TimeOfDay >= dailyTimeIst ? "scheduled" : "catch-up", nowIst, ct, decision.Reason);
                        }
                        else if (decision.Run && !catchUp)
                        {
                            // Exact-time mode: only fire inside the window after the scheduled time.
                            if (nowIst.TimeOfDay >= dailyTimeIst && nowIst.TimeOfDay < dailyTimeIst + TriggerWindow)
                                await TriggerAsync("scheduled", nowIst, ct, decision.Reason);
                        }
                        else if (DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatEvery)
                        {
                            _lastHeartbeatUtc = DateTime.UtcNow;
                            _logger.LogInformation(
                                "SyncHostedService alive — now {now} IST. No reload needed: {reason}.",
                                nowIst.ToString("dd.MM.yyyy HH:mm"), decision.Reason);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected error (e.g. database briefly unreachable) must NOT end this loop —
                    // an unhandled exception in a BackgroundService stops the whole host by default.
                    _logger.LogError(ex, "SyncHostedService check error — will retry in {min} min.", CheckEvery.TotalMinutes);
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("SyncHostedService stopping.");
        }
    }

    private async Task TriggerAsync(string trigger, DateTime nowIst, CancellationToken ct, string? reason = null)
    {
        _logger.LogInformation(
            "Reload trigger '{trigger}' fired at {time} IST — FULL reload (ERP check, truncate, re-fetch, validate). Reason: {reason}",
            trigger, nowIst.ToString("dd.MM.yyyy HH:mm:ss"), reason ?? "n/a");

        var started = await _runner.RunAsync(trigger, ct);
        if (!started)
            _logger.LogWarning("Reload ({trigger}) skipped — a reload is already running.", trigger);
    }
}