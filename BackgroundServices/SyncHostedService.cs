// ─────────────────────────────────────────────────────────────
// REPLACE your existing SyncHostedService.cs with this full file
// Changes: added RunVehicleSalesSyncAsync + backfill for VSR
// ─────────────────────────────────────────────────────────────

using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

public class SyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncHostedService> _logger;

    public SyncHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<SyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SyncHostedService starting...");

        // One-time backfill on startup (runs in background, won't block)
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            await RunBackfillAsync(ct);           // Service history backfill
            await RunVehicleSalesBackfillAsync(ct); // Vehicle sales backfill
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            // 01:00 UTC — dealer sync
            var dealerHour = _config.GetValue<int>("SyncSettings:DealerSyncHour", 1);
            var dealerMin  = _config.GetValue<int>("SyncSettings:DealerSyncMinute", 0);
            var nextDealer = GetNextRun(now, dealerHour, dealerMin);

            // 02:00 UTC — service history sync
            var jobHour = _config.GetValue<int>("SyncSettings:DailyJobSyncHour", 2);
            var jobMin  = _config.GetValue<int>("SyncSettings:DailyJobSyncMinute", 0);
            var nextJob = GetNextRun(now, jobHour, jobMin);

            // 03:00 UTC — vehicle sales sync
            var vsrHour = _config.GetValue<int>("SyncSettings:VehicleSalesSyncHour", 3);
            var vsrMin  = _config.GetValue<int>("SyncSettings:VehicleSalesSyncMinute", 0);
            var nextVsr = GetNextRun(now, vsrHour, vsrMin);

            // Sleep until the earliest of the three
            var nextRun = new[] { nextDealer, nextJob, nextVsr }.Min();
            var delay = nextRun - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(60);

            _logger.LogInformation("Next sync at {next} UTC (in {delay:hh\\:mm\\:ss})",
                nextRun.ToString("yyyy-MM-dd HH:mm"), delay);

            await Task.Delay(delay, ct);
            if (ct.IsCancellationRequested) break;

            var runNow = DateTime.UtcNow;

            if (Math.Abs((runNow - GetNextRun(runNow.AddSeconds(-70), dealerHour, dealerMin)).TotalMinutes) < 2)
                await RunDealerSyncAsync(ct);

            if (Math.Abs((runNow - GetNextRun(runNow.AddSeconds(-70), jobHour, jobMin)).TotalMinutes) < 2)
                await RunDailyJobSyncAsync(ct);

            if (Math.Abs((runNow - GetNextRun(runNow.AddSeconds(-70), vsrHour, vsrMin)).TotalMinutes) < 2)
                await RunVehicleSalesSyncAsync(ct);
        }
    }

    // ── Dealer sync ─────────────────────────────────────────
    private async Task RunDealerSyncAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Scheduled] Starting dealer sync...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncAllDealersAsync();
            _logger.LogInformation("[Scheduled] Dealer sync done: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Scheduled] Dealer sync error");
        }
    }

    // ── Service history sync ─────────────────────────────────
    private async Task RunDailyJobSyncAsync(CancellationToken ct)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);

            _logger.LogInformation("[Scheduled] Starting service history sync for {y} and {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

            var r1 = await svc.SyncServiceHistoryForDateAsync(yesterday);
            var r2 = await svc.SyncServiceHistoryForDateAsync(today);

            _logger.LogInformation(
                "[Scheduled] Service sync done: yesterday={y_ins}ins/{y_upd}upd, today={t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Scheduled] Service history sync error");
        }
    }

    // ── Vehicle Sales sync (NEW) ─────────────────────────────
    private async Task RunVehicleSalesSyncAsync(CancellationToken ct)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);

            _logger.LogInformation("[Scheduled] Starting vehicle sales sync for {y} and {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

            var r1 = await svc.SyncVehicleSalesForDateAsync(yesterday);
            var r2 = await svc.SyncVehicleSalesForDateAsync(today);

            _logger.LogInformation(
                "[Scheduled] Vehicle sales sync done: yesterday={y_ins}ins/{y_upd}upd, today={t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Scheduled] Vehicle sales sync error");
        }
    }

    // ── Backfills ────────────────────────────────────────────
    private async Task RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Service history backfill starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillHistoricalDataAsync(ct: ct);
            _logger.LogInformation("[Backfill] Service history complete: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] Service history error"); }
    }

    private async Task RunVehicleSalesBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Vehicle sales backfill starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillVehicleSalesAsync(ct: ct);
            _logger.LogInformation("[Backfill] Vehicle sales complete: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] Vehicle sales error"); }
    }

    private static DateTime GetNextRun(DateTime from, int hour, int minute)
    {
        var candidate = from.Date.AddHours(hour).AddMinutes(minute);
        if (candidate <= from) candidate = candidate.AddDays(1);
        return candidate;
    }
}
