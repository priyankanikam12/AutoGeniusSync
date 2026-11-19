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
    long RecordsInserted,
    string? Mode = null,      // Window (rolling upsert) | Full (re-check all months, upsert) | Missing (empty tables only) | Historical (resume checkpoints)
    long RecordsUpdated = 0);

// Manual / catch-up request for the "load missing tables" mode. Tables: djr, vsr, vdr, lor (null = every empty table).
public sealed record MissingRequest(string[]? Tables = null, DateTime? From = null, DateTime? To = null);

// Answer of NightlyReloadRunner.CheckCatchUpAsync: should a reload be started now, and why / why not.
public sealed record CatchUpDecision(bool Run, string Reason, bool Full = false, bool Missing = false, bool Fy = false);

// Single place that starts ANY ERP data sync: nightly window, full re-check, missing-tables load,
// historical checkpoint sync and the manual backfill endpoints. Used by the daily timer
// (SyncHostedService) and every manual endpoint, so two syncs can never run at the same time —
// not in this process and (through SyncCoordinator's database lock) not in a second IIS worker
// process during an overlapped recycle. The second caller is told "already running".
public class NightlyReloadRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<NightlyReloadRunner> _logger;
    private readonly SyncCoordinator _coordinator;
    private readonly object _stateLock = new();
    private ReloadStatus _status = new(false, null, null, null, null, null, 0, 0);

    public NightlyReloadRunner(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<NightlyReloadRunner> logger,
                               SyncCoordinator coordinator)
    {
        _coordinator = coordinator;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    // appsettings "Sync:Mode": "Window" (default) = daily rolling-window upsert, NO truncate;
    //                          "Full"              = truncate the 4 tables and reload from HistoricalStartDate.
    public static bool IsFullMode(string? text) => string.Equals(text?.Trim(), "Full", StringComparison.OrdinalIgnoreCase);
    public bool ConfiguredFull => IsFullMode(_config["Sync:Mode"]);

    // "Sync:Mode": "FyRefresh" = every night replace the CURRENT FINANCIAL YEAR's rows of the 4 ERP tables with
    // freshly fetched data (staging + transactional swap; older FYs untouched). See DataSyncService.FyRefresh.cs.
    public static bool IsFyMode(string? text) => string.Equals(text?.Trim(), "FyRefresh", StringComparison.OrdinalIgnoreCase);
    public bool ConfiguredFy => IsFyMode(_config["Sync:Mode"]);

    public async Task<bool> IsTodaysFyRefreshCompletedAsync(DateTime nowIst, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateOnly.FromDateTime(nowIst);
        return await db.DmsFyRefreshStates.AsNoTracking()
            .AnyAsync(x => x.RunDate == today && x.Status == "Completed", ct);
    }

    public Task<bool> RunFyRefreshAsync(string trigger, CancellationToken ct)
        => RunLockedAsync(trigger, "FyRefresh", ct, svc => svc.RunFyRefreshAsync(ct));

    // FY mode: run when today's refresh is not completed and the scheduled time has passed, or when an
    // unfinished run exists (stopped by an IIS recycle / still running from last night) — at any hour.
    private async Task<CatchUpDecision> CheckFyCatchUpAsync(DateTime nowIst, TimeSpan dailyTimeIst, int maxAttemptsPerDay, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nowUtc = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(nowIst);
        var todayStartUtc = nowIst.Date - new TimeSpan(5, 30, 0);

        var last = await db.DmsFyRefreshStates.AsNoTracking().OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (last != null && last.RunDate == today && last.Status == "Completed")
            return new CatchUpDecision(false, "today's current-FY refresh is completed");

        var logs = await db.DmsSyncLogs.AsNoTracking()
            .Where(l => l.SyncType == "FyRefresh" && l.StartedAt >= todayStartUtc)
            .Select(l => new { l.Status, l.StartedAt }).ToListAsync(ct);
        if (logs.Any(l => l.Status == "Running" && l.StartedAt > nowUtc.AddMinutes(-30)))
            return new CatchUpDecision(false, "an FY refresh started in the last 30 min (maybe another process)");

        var unfinished = last != null && last.Status == "Fetching";
        if (!unfinished && nowIst.TimeOfDay < dailyTimeIst)
            return new CatchUpDecision(false, "scheduled time not reached yet");
        // FY runs resume from staging (only missing/failed units are fetched again), so they get their own,
        // higher cap: FyRefresh:MaxAttemptsPerDay (default 6).
        var fyCap = Math.Max(1, _config.GetValue("FyRefresh:MaxAttemptsPerDay", 6));
        if (logs.Count >= fyCap)
            return new CatchUpDecision(false, $"daily attempt cap reached ({logs.Count}/{fyCap}, FyRefresh:MaxAttemptsPerDay)");
        if (logs.Count > 0 && logs.Max(l => l.StartedAt) > nowUtc.AddMinutes(-15))
            return new CatchUpDecision(false, "last attempt started less than 15 min ago");

        return new CatchUpDecision(true, unfinished ? "an unfinished FY refresh exists — resuming" : "today's FY refresh not done yet", Fy: true);
    }

    public bool IsRunning { get { lock (_stateLock) return _status.IsRunning || _coordinator.IsHeldLocally; } }

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
        if (ConfiguredFy)
            return await CheckFyCatchUpAsync(nowIst, dailyTimeIst, maxAttemptsPerDay, ct);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var nowUtc        = DateTime.UtcNow;
        var todayStartUtc = nowIst.Date - new TimeSpan(5, 30, 0);   // 00:00 IST today, expressed in UTC

        var types  = new[] { "NightlyFullReload", "DailyWindowSync", "MissingTablesLoad" };
        var todays = await db.DmsSyncLogs
            .Where(l => types.Contains(l.SyncType!) && l.StartedAt >= todayStartUtc)
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

        // Window / Missing runs are idempotent (upsert / insert-only), so a PartialFailure there is worth
        // retrying (capped below); a PartialFailure of the truncating Full reload counts as done — re-truncating is costly.
        bool completedToday = todays.Any(l => l.Status == "Success"
                                           || (l.Status == "PartialFailure" && l.SyncType == "NightlyFullReload"));

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

        // Empty table(s) → load ONLY the empty tables, insert-only, WITHOUT truncating the tables that
        // already hold data (a Full reload would delete e.g. 4,59,465 good ServiceHistory rows and spend
        // hours re-fetching them). Otherwise follow Sync:Mode (Window by default).
        if (emptyTables)
            return new CatchUpDecision(true, why, Full: false, Missing: true);
        return new CatchUpDecision(true, why, Full: ConfiguredFull);
    }

    // Runs the sync unless one is already running. forceFull: true = Full, false = Window, null = Sync:Mode.
    // missing != null = "load missing tables" mode (no truncate), which wins over forceFull.
    // Returns false (and does nothing) when another run holds the gate. Never throws.
    public Task<bool> RunAsync(string trigger, CancellationToken ct, bool? forceFull = null, MissingRequest? missing = null)
    {
        var full = missing == null && (forceFull ?? ConfiguredFull);
        var mode = missing != null ? "Missing" : full ? "Full" : "Window";
        return RunLockedAsync(trigger, mode, ct, async svc => missing != null
            ? await svc.RunMissingTablesLoadAsync(missing.Tables, missing.From, missing.To, ct)
            : full
                ? await svc.RunNightlyFullReloadAsync(ct)
                : await svc.RunDailyWindowSyncAsync(ct));
    }

    // Historical month-wise / dealer-wise sync from HistoricalStartDate, resuming from DMS_SyncCheckpoint.
    // deadlineUtc: stop cleanly at that time (the hosted service passes the next nightly start).
    public Task<bool> RunHistoricalAsync(string trigger, CancellationToken ct, DateTime? deadlineUtc = null,
                                         IReadOnlyCollection<string>? apis = null)
        => RunLockedAsync(trigger, "Historical", ct, svc => svc.RunHistoricalCheckpointSyncAsync(deadlineUtc, ct, apis));

    // Any other sync body (e.g. the per-API backfill endpoints) run under the same single-sync lock.
    public Task<bool> RunCustomAsync(string trigger, string mode, CancellationToken ct, Func<DataSyncService, Task<DTOs.SyncResult>> body)
        => RunLockedAsync(trigger, mode, ct, body);

    // Rows a killed process left as "Running" (the lock is held, so nothing else is running now).
    private static readonly string[] RunnerLogTypes =
    {
        "NightlyFullReload", "DailyWindowSync", "MissingTablesLoad", "HistoricalCheckpointSync", "FyRefresh",
        "BackfillHistorical", "BackfillVehicleSales", "BackfillVehicleDispatches", "BackfillLineOrderReport"
    };

    private async Task MarkStaleRunsInterruptedAsync(DateTime beforeUtc)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var n = await db.DmsSyncLogs
                .Where(l => l.Status == "Running" && l.StartedAt < beforeUtc && RunnerLogTypes.Contains(l.SyncType!))
                .ExecuteUpdateAsync(x => x
                    .SetProperty(l => l.Status, "Interrupted")
                    .SetProperty(l => l.CompletedAt, DateTime.UtcNow));
            if (n > 0) _logger.LogWarning("{n} sync-log row(s) left 'Running' by a stopped process were marked Interrupted.", n);
        }
        catch (Exception ex) { _logger.LogWarning("Could not mark stale sync-log rows: {msg}", ex.Message); }
    }

    private async Task<bool> RunLockedAsync(string trigger, string mode, CancellationToken ct,
                                            Func<DataSyncService, Task<DTOs.SyncResult>> body)
    {
        await using var handle = await _coordinator.TryAcquireAsync($"{mode}:{trigger}", CancellationToken.None);
        if (handle == null)
            return false;

        var startedUtc = DateTime.UtcNow;
        await MarkStaleRunsInterruptedAsync(startedUtc);
        try
        {
            SetStatus(new ReloadStatus(true, trigger, startedUtc, null, "Running", null, 0, 0, mode));
            _logger.LogInformation("{mode} sync START (trigger: {trigger}).", mode, trigger);

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var result = await body(svc);

            var outcome = string.IsNullOrEmpty(result.Error) ? "Success" : "Problems";
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, outcome,
                result.Error, result.RecordsFetched, result.RecordsInserted, mode, result.RecordsUpdated));

            if (outcome == "Success")
                _logger.LogInformation("{mode} sync END — Success (trigger: {trigger}): {fet} fetched, {ins} inserted, {upd} updated.",
                    mode, trigger, result.RecordsFetched, result.RecordsInserted, result.RecordsUpdated);
            else
                _logger.LogError("{mode} sync END — completed WITH PROBLEMS (trigger: {trigger}): {msg}",
                    mode, trigger, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Interrupted",
                "App was shutting down / IIS recycle. Nothing was deleted; the sync resumes from the checkpoint on the next run.",
                0, 0, mode));
            _logger.LogWarning("{mode} sync interrupted by shutdown (trigger: {trigger}).", mode, trigger);
        }
        catch (Exception ex)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Failed", ex.Message, 0, 0, mode));
            _logger.LogError(ex, "{mode} sync crashed (trigger: {trigger}).", mode, trigger);
        }

        return true;
    }
}