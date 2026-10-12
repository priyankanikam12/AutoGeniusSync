using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

// Daily timer for the nightly FULL reload (ERP check → truncate → re-fetch → validate).
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

    private DateTime _lastReloadDateIst = DateTime.MinValue;
    private DateTime _lastHeartbeatUtc  = DateTime.MinValue;

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

        var startIst = DateTime.UtcNow.Add(IstOffset);
        _logger.LogInformation(
            "SyncHostedService starting at {now} IST — daily FULL reload at {time} IST. RunOnStartup={startup}. Next scheduled run: {next} IST.",
            startIst.ToString("dd.MM.yyyy HH:mm:ss"),
            dailyTimeIst.ToString(@"hh\:mm"),
            runOnStartup,
            NextRunIst(startIst, dailyTimeIst).ToString("dd.MM.yyyy HH:mm"));

        try
        {
            // ── Run once immediately ("start now") ────────────────────────────
            if (runOnStartup)
            {
                var nowIst = DateTime.UtcNow.Add(IstOffset);
                _lastReloadDateIst = nowIst.Date;   // today counts as done → no second run in today's window
                await TriggerAsync("startup", nowIst, ct);
            }

            // ── Daily schedule ────────────────────────────────────────────────
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var nowIst = DateTime.UtcNow.Add(IstOffset);
                    var t      = nowIst.TimeOfDay;

                    bool inWindow = t >= dailyTimeIst && t < dailyTimeIst + TriggerWindow;

                    if (inWindow && _lastReloadDateIst != nowIst.Date)
                    {
                        _lastReloadDateIst = nowIst.Date;
                        await TriggerAsync("scheduled", nowIst, ct);
                    }
                    else if (DateTime.UtcNow - _lastHeartbeatUtc >= HeartbeatEvery)
                    {
                        _lastHeartbeatUtc = DateTime.UtcNow;
                        _logger.LogInformation(
                            "SyncHostedService alive — now {now} IST, next scheduled reload {next} IST.",
                            nowIst.ToString("dd.MM.yyyy HH:mm"),
                            NextRunIst(nowIst, dailyTimeIst).ToString("dd.MM.yyyy HH:mm"));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected error must NOT end this loop (an unhandled exception in a
                    // BackgroundService stops the whole host by default).
                    _logger.LogError(ex, "SyncHostedService loop error — continuing.");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("SyncHostedService stopping.");
        }
    }

    // Next time the scheduled reload will fire, in IST (for log lines only).
    private DateTime NextRunIst(DateTime nowIst, TimeSpan dailyTimeIst)
    {
        var next = nowIst.Date + dailyTimeIst;
        if (_lastReloadDateIst == nowIst.Date || nowIst.TimeOfDay >= dailyTimeIst + TriggerWindow)
            next = next.AddDays(1);
        return next;
    }

    private async Task TriggerAsync(string trigger, DateTime nowIst, CancellationToken ct)
    {
        _logger.LogInformation(
            "Reload trigger '{trigger}' fired at {time} IST — FULL reload (ERP check, truncate, re-fetch, validate).",
            trigger, nowIst.ToString("dd.MM.yyyy HH:mm:ss"));

        var started = await _runner.RunAsync(trigger, ct);
        if (!started)
            _logger.LogWarning("Reload ({trigger}) skipped — a reload is already running.", trigger);
    }
}