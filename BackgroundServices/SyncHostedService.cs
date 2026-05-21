using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

/// <summary>
/// Runs two scheduled tasks:
/// 1. Every day at 02:00 UTC → sync yesterday's + today's service history
/// 2. Every day at 01:00 UTC → refresh dealer list from all pincodes
/// Also runs a one-time backfill on first startup if needed.
/// </summary>
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

        // Run initial backfill in background (don't block startup)
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct); // wait for app to be ready
            await RunBackfillAsync(ct);
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            // ── Dealer sync at 01:00 UTC ──────────────────
            var dealerHour = _config.GetValue<int>("SyncSettings:DealerSyncHour", 1);
            var dealerMin  = _config.GetValue<int>("SyncSettings:DealerSyncMinute", 0);
            var nextDealer = GetNextRun(now, dealerHour, dealerMin);

            // ── Service history sync at 02:00 UTC ─────────
            var jobHour = _config.GetValue<int>("SyncSettings:DailyJobSyncHour", 2);
            var jobMin  = _config.GetValue<int>("SyncSettings:DailyJobSyncMinute", 0);
            var nextJob = GetNextRun(now, jobHour, jobMin);

            // Sleep until the earlier of the two
            var nextRun = nextDealer < nextJob ? nextDealer : nextJob;
            var delay = nextRun - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(60);

            _logger.LogInformation("Next sync at {next} UTC (in {delay:hh\\:mm\\:ss})",
                nextRun.ToString("yyyy-MM-dd HH:mm"), delay);

            await Task.Delay(delay, ct);
            if (ct.IsCancellationRequested) break;

            var runNow = DateTime.UtcNow;

            // Dealer sync
            if (Math.Abs((runNow - GetNextRun(runNow.AddSeconds(-70), dealerHour, dealerMin)).TotalMinutes) < 2)
                await RunDealerSyncAsync(ct);

            // Service history sync
            if (Math.Abs((runNow - GetNextRun(runNow.AddSeconds(-70), jobHour, jobMin)).TotalMinutes) < 2)
                await RunDailyJobSyncAsync(ct);
        }
    }

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

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Checking for historical data to backfill...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillHistoricalDataAsync(ct: ct);
            _logger.LogInformation("[Backfill] Complete: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Backfill] Error during historical backfill");
        }
    }

    private static DateTime GetNextRun(DateTime from, int hour, int minute)
    {
        var candidate = from.Date.AddHours(hour).AddMinutes(minute);
        if (candidate <= from) candidate = candidate.AddDays(1);
        return candidate;
    }
}
