using AutoGeniusSync.Data;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Services;

// Snapshot of the most recent nightly reload — returned by GET api/admin/reload/status.
public sealed record ReloadStatus(
    bool IsRunning,
    string? Trigger,          // scheduled | startup | manual-api
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Outcome,          // Running | Success | Problems | Interrupted | Failed
    string? Message,          // detail: failed chunks, empty tables, "FAILED BEFORE truncate", ...
    long RecordsFetched,
    long RecordsInserted);

// Answer of NightlyReloadRunner.CheckCatchUpAsync: should a reload be started now, and why / why not.
public sealed record CatchUpDecision(bool Run, string Reason);

// Single place that starts the nightly full reload. Used by BOTH the daily timer
// (SyncHostedService) and the manual / Task-Scheduler endpoint (NightlyReloadController),
// so the two can never run a reload at the same time — the second caller is told
// "already running" instead of truncating tables underneath the first one.
public class NightlyReloadRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyReloadRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private ReloadStatus _status = new(false, null, null, null, null, null, 0, 0);

    public NightlyReloadRunner(IServiceScopeFactory scopeFactory, ILogger<NightlyReloadRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning { get { lock (_stateLock) return _status.IsRunning; } }

    public ReloadStatus GetStatus() { lock (_stateLock) return _status; }

    private void SetStatus(ReloadStatus s) { lock (_stateLock) _status = s; }

    // ── SELF-HEALING CHECK ────────────────────────────────────────────────────────────────────
    // The daily timer alone is fragile under IIS (process stopped/recycled at 00:00 → run silently
    // missed, or killed half-way after the truncate → tables left empty). This check looks at the
    // database, not at the clock, so a missed or broken run is picked up automatically:
    //
    //   run now  when  NO completed reload (Success / PartialFailure) exists for today (IST)
    //                  AND ( the scheduled time today has passed  OR  any of the 4 tables is empty )
    //            OR    a reload completed today but any of the 4 tables is empty (data missing)
    //   …but never when a reload is running, one started in the last 30 min (could be another
    //   process), the last attempt started < 15 min ago, or today's attempt cap is reached.
    //
    // Also marks "Running" rows left behind by a killed process as "Interrupted".
    public async Task<CatchUpDecision> CheckCatchUpAsync(
        DateTime nowIst, TimeSpan dailyTimeIst, int maxAttemptsPerDay, CancellationToken ct)
    {
        if (IsRunning)
            return new CatchUpDecision(false, "a reload is running in this process");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var nowUtc        = DateTime.UtcNow;
        var todayStartUtc = nowIst.Date - new TimeSpan(5, 30, 0);   // 00:00 IST today, expressed in UTC

        var todays = await db.DmsSyncLogs
            .Where(l => l.SyncType == "NightlyFullReload" && l.StartedAt >= todayStartUtc)
            .ToListAsync(ct);

        // Rows still "Running" although nothing runs in this process = the process was stopped mid-run.
        var staleCutoff = nowUtc.AddMinutes(-30);
        var stale = todays.Where(l => l.Status == "Running" && l.StartedAt < staleCutoff).ToList();
        if (stale.Count > 0)
        {
            foreach (var l in stale)
            {
                l.Status = "Interrupted";
                l.ErrorMessage = "Process stopped mid-run (marked by catch-up check). Last progress: " + l.ErrorMessage;
                if (l.ErrorMessage.Length > 1800) l.ErrorMessage = l.ErrorMessage[..1800];
            }
            await db.SaveChangesAsync(ct);
        }

        if (todays.Any(l => l.Status == "Running" && l.StartedAt >= staleCutoff))
            return new CatchUpDecision(false, "a reload started in the last 30 min (maybe another process)");

        bool completedToday = todays.Any(l => l.Status == "Success" || l.Status == "PartialFailure");

        bool emptyTables =
               !await db.DmsServiceHistories.AnyAsync(ct)
            || !await db.DmsVehicleSales.AnyAsync(ct)
            || !await db.DmsVehicleDispatches.AnyAsync(ct)
            || !await db.DmsLineOrderReports.AnyAsync(ct);

        bool pastSchedule = nowIst.TimeOfDay >= dailyTimeIst;

        string? why = null;
        if (!completedToday && emptyTables)       why = "a data table is EMPTY and no reload completed today";
        else if (!completedToday && pastSchedule) why = "no reload completed today and the scheduled time has passed";
        else if (completedToday && emptyTables)   why = "a reload completed today but a data table is EMPTY";

        if (why == null)
            return new CatchUpDecision(false, completedToday ? "today's reload already completed" : "scheduled time not reached yet");

        if (todays.Count >= maxAttemptsPerDay)
            return new CatchUpDecision(false, $"daily attempt cap reached ({todays.Count}/{maxAttemptsPerDay}); needed because {why}");

        if (todays.Count > 0 && todays.Max(l => l.StartedAt) > nowUtc.AddMinutes(-15))
            return new CatchUpDecision(false, "last attempt started less than 15 min ago; needed because " + why);

        return new CatchUpDecision(true, why);
    }

    // Runs the full reload unless one is already running. Returns false (and does nothing)
    // when another reload holds the gate. Never throws.
    public async Task<bool> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return false;

        var startedUtc = DateTime.UtcNow;
        try
        {
            SetStatus(new ReloadStatus(true, trigger, startedUtc, null, "Running", null, 0, 0));
            _logger.LogInformation("Nightly full reload START (trigger: {trigger}).", trigger);

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var result = await svc.RunNightlyFullReloadAsync(ct);

            var outcome = string.IsNullOrEmpty(result.Error) ? "Success" : "Problems";
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, outcome,
                result.Error, result.RecordsFetched, result.RecordsInserted));

            if (outcome == "Success")
                _logger.LogInformation("Nightly full reload END — Success (trigger: {trigger}): {fet} fetched, {ins} inserted.",
                    trigger, result.RecordsFetched, result.RecordsInserted);
            else
                _logger.LogError("Nightly full reload END — completed WITH PROBLEMS (trigger: {trigger}): {msg}",
                    trigger, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Interrupted",
                "App was shutting down. If tables were already truncated they are only partly loaded — run the reload again.", 0, 0));
            _logger.LogWarning("Nightly full reload interrupted by shutdown (trigger: {trigger}).", trigger);
        }
        catch (Exception ex)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Failed", ex.Message, 0, 0));
            _logger.LogError(ex, "Nightly full reload crashed (trigger: {trigger}).", trigger);
        }
        finally
        {
            _gate.Release();
        }

        return true;
    }
}