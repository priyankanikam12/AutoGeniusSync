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
        _logger.LogInformation("SyncHostedService started.");

        // ── On startup: immediately sync today + yesterday ──
        await RunStartupSyncAsync(ct);

        // ── Then loop every N minutes for real-time today sync ──
        var intervalMinutes = _config.GetValue<int>("SyncSettings:RealtimeSyncIntervalMinutes", 30);

        // ── Also schedule nightly backfill at 01:00 UTC ──
        _ = Task.Run(() => RunNightlyBackfillLoopAsync(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct);
                if (ct.IsCancellationRequested) break;

                await RunRealtimeSyncAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Realtime sync loop error. Will retry next interval.");
            }
        }

        _logger.LogInformation("SyncHostedService stopped.");
    }

    // ─────────────────────────────────────────────────────────
    // STARTUP: runs immediately when app starts
    // ─────────────────────────────────────────────────────────
    private async Task RunStartupSyncAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Startup sync begin ===");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

            var today     = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);

            _logger.LogInformation("Startup: syncing {y} and {t}",
                yesterday.ToString("dd-MM-yyyy"), today.ToString("dd-MM-yyyy"));

            // Service history — yesterday then today
            var r1 = await svc.SyncServiceHistoryForDateAsync(yesterday);
            var r2 = await svc.SyncServiceHistoryForDateAsync(today);
            _logger.LogInformation("Startup ServiceHistory: yesterday={yi}ins/{yu}upd  today={ti}ins/{tu}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);

            // Vehicle sales — yesterday then today
            var r3 = await svc.SyncVehicleSalesForDateAsync(yesterday);
            var r4 = await svc.SyncVehicleSalesForDateAsync(today);
            _logger.LogInformation("Startup VehicleSales: yesterday={yi}ins/{yu}upd  today={ti}ins/{tu}upd",
                r3.RecordsInserted, r3.RecordsUpdated, r4.RecordsInserted, r4.RecordsUpdated);

            // Dealers in background — slow pincode loop, don't block realtime sync
            _ = Task.Run(() => RunDealerSyncAsync(ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup sync failed");
        }
        _logger.LogInformation("=== Startup sync complete ===");
    }

    // ─────────────────────────────────────────────────────────
    // REALTIME: runs every 30 min (configurable), syncs today
    // ─────────────────────────────────────────────────────────
    private async Task RunRealtimeSyncAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== Realtime sync begin {time} UTC ===", DateTime.UtcNow.ToString("HH:mm"));
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

            var today = DateTime.UtcNow.Date;

            // In the first 3 hours of the day, also re-sync yesterday
            // because ERP data for yesterday often arrives late
            if (DateTime.UtcNow.Hour < 3)
            {
                var yesterday = today.AddDays(-1);
                _logger.LogInformation("Early hour — also re-syncing yesterday: {d}",
                    yesterday.ToString("dd-MM-yyyy"));
                await svc.SyncServiceHistoryForDateAsync(yesterday);
                await svc.SyncVehicleSalesForDateAsync(yesterday);
            }

            var r1 = await svc.SyncServiceHistoryForDateAsync(today);
            var r2 = await svc.SyncVehicleSalesForDateAsync(today);

            _logger.LogInformation(
                "=== Realtime sync done: SH={si}ins/{su}upd  VSR={vi}ins/{vu}upd ===",
                r1.RecordsInserted, r1.RecordsUpdated,
                r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime sync error");
        }
    }

    // ─────────────────────────────────────────────────────────
    // NIGHTLY BACKFILL LOOP: runs once a day at 01:00 UTC
    // fills any historical gaps without blocking realtime sync
    // ─────────────────────────────────────────────────────────
    private async Task RunNightlyBackfillLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now  = DateTime.UtcNow;
                var next = now.Date.AddHours(1); // 01:00 UTC today
                if (next <= now) next = next.AddDays(1);  // already past → tomorrow

                var delay = next - now;
                _logger.LogInformation("Nightly backfill scheduled at {next} UTC (in {h}h {m}m)",
                    next.ToString("yyyy-MM-dd HH:mm"),
                    (int)delay.TotalHours, delay.Minutes);

                await Task.Delay(delay, ct);
                if (ct.IsCancellationRequested) break;

                _logger.LogInformation("=== Nightly backfill begin ===");
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

                // Dealer sync at nightly run
                await RunDealerSyncAsync(ct);

                // Backfill any missing historical dates
                var shResult  = await svc.BackfillHistoricalDataAsync(ct: ct);
                var vsrResult = await svc.BackfillVehicleSalesAsync(ct: ct);

                _logger.LogInformation(
                    "=== Nightly backfill done: SH={si}ins  VSR={vi}ins ===",
                    shResult.RecordsInserted, vsrResult.RecordsInserted);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly backfill error. Will retry tomorrow.");
                await Task.Delay(TimeSpan.FromHours(1), ct); // wait before retrying
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // DEALER SYNC: slow (loops all pincodes), always background
    // ─────────────────────────────────────────────────────────
    private async Task RunDealerSyncAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Dealer sync starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncAllDealersAsync();
            _logger.LogInformation("Dealer sync done: {i} inserted, {u} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dealer sync error");
        }
    }
}