using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

public class SyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncHostedService> _logger;

    // Tracks last run time for each sync type so we don't overlap
    private DateTime _lastDealerSync       = DateTime.MinValue;
    private DateTime _lastCallCentreSync   = DateTime.MinValue;
    private DateTime _lastServiceHistory   = DateTime.MinValue;
    private DateTime _lastVehicleSales     = DateTime.MinValue;
    private DateTime _lastVehicleDispatches = DateTime.MinValue;
    private DateTime _lastLineOrderReport = DateTime.MinValue;

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
        _logger.LogInformation("SyncHostedService starting — realtime mode...");

        // On startup: run backfills first, then start the realtime loop
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            // Dealers first (needed for VSR)
            await RunDealerSyncAsync(ct);
            await RunCallCentreDealerSyncAsync(ct);

            // Historical backfills (skip already-done dates)
            await RunBackfillAsync(ct);
            await RunVehicleSalesBackfillAsync(ct);
            await RunVehicleDispatchesBackfillAsync(ct);
            await RunLineOrderBackfillAsync(ct);

        }, ct);

        // Read interval from config (default 30 minutes)
        var intervalMinutes = _config.GetValue<int>("SyncSettings:RealtimeSyncIntervalMinutes", 30);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        _logger.LogInformation("Realtime sync interval: every {min} minutes", intervalMinutes);

        // Separate intervals per sync type (in minutes)
        var dealerIntervalMin    = _config.GetValue<int>("SyncSettings:DealerIntervalMinutes", 1440);   // once a day
        var callCentreIntervalMin = _config.GetValue<int>("SyncSettings:CallCentreIntervalMinutes", 1440);
        var serviceHistoryMin    = _config.GetValue<int>("SyncSettings:ServiceHistoryIntervalMinutes", intervalMinutes);
        var vehicleSalesMin      = _config.GetValue<int>("SyncSettings:VehicleSalesIntervalMinutes", intervalMinutes);
        var vehicleDispatchMin   = _config.GetValue<int>("SyncSettings:VehicleDispatchIntervalMinutes", intervalMinutes);
        var lineOrderMin = _config.GetValue<int>("SyncSettings:LineOrderIntervalMinutes", intervalMinutes);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            // Run each sync type based on its own interval
            var tasks = new List<Task>();

            if ((now - _lastDealerSync).TotalMinutes >= dealerIntervalMin)
            {
                _lastDealerSync = now;
                tasks.Add(RunDealerSyncAsync(ct));
            }

            if ((now - _lastCallCentreSync).TotalMinutes >= callCentreIntervalMin)
            {
                _lastCallCentreSync = now;
                tasks.Add(RunCallCentreDealerSyncAsync(ct));
            }

            if ((now - _lastServiceHistory).TotalMinutes >= serviceHistoryMin)
            {
                _lastServiceHistory = now;
                tasks.Add(RunDailyJobSyncAsync(ct));
            }

            if ((now - _lastVehicleSales).TotalMinutes >= vehicleSalesMin)
            {
                _lastVehicleSales = now;
                tasks.Add(RunVehicleSalesSyncAsync(ct));
            }

            if ((now - _lastVehicleDispatches).TotalMinutes >= vehicleDispatchMin)
            {
                _lastVehicleDispatches = now;
                tasks.Add(RunVehicleDispatchesSyncAsync(ct));
            }

            if ((now - _lastLineOrderReport).TotalMinutes >= lineOrderMin)
            {
                _lastLineOrderReport = now;
                tasks.Add(RunLineOrderReportSyncAsync(ct));
            }

            // Run all due syncs in parallel
            if (tasks.Any())
                await Task.WhenAll(tasks);

            // Sleep 1 minute between checks
            _logger.LogDebug("Sync loop sleeping 1 minute...");
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task RunLineOrderBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] LOR starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillLineOrderReportAsync(ct: ct);
            _logger.LogInformation("[Backfill] LOR done: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] LOR error"); }
    }

    // ── Dealer syncs ─────────────────────────────────────────
    private async Task RunDealerSyncAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Sync] Dealer sync starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncAllDealersAsync();
            _logger.LogInformation("[Sync] Dealer: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Dealer sync error");
        }
    }

    private async Task RunCallCentreDealerSyncAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Sync] CallCentre dealer sync starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncCallCentreDealersAsync();
            _logger.LogInformation("[Sync] CallCentre dealers: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] CallCentre dealer sync error");
        }
    }

    // ── Service history — today + yesterday ──────────────────
    private async Task RunDailyJobSyncAsync(CancellationToken ct)
    {
        try
        {
            var today     = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);
            _logger.LogInformation("[Sync] Service history: {y} and {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r1 = await svc.SyncServiceHistoryForDateAsync(yesterday);
            var r2 = await svc.SyncServiceHistoryForDateAsync(today);
            _logger.LogInformation("[Sync] Service history — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Service history error");
        }
    }

    // ── Vehicle sales — today + yesterday ────────────────────
    private async Task RunVehicleSalesSyncAsync(CancellationToken ct)
    {
        try
        {
            var today     = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);
            _logger.LogInformation("[Sync] Vehicle sales: {y} and {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r1 = await svc.SyncVehicleSalesForDateAsync(yesterday);
            var r2 = await svc.SyncVehicleSalesForDateAsync(today);
            _logger.LogInformation("[Sync] VSR — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Vehicle sales error");
        }
    }

    // ── Vehicle dispatches — today + yesterday ───────────────
    private async Task RunVehicleDispatchesSyncAsync(CancellationToken ct)
    {
        try
        {
            var today     = DateTime.UtcNow.Date;
            var yesterday = today.AddDays(-1);
            _logger.LogInformation("[Sync] Vehicle dispatches: {y} and {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r1 = await svc.SyncVehicleDispatchesForDateAsync(yesterday);
            var r2 = await svc.SyncVehicleDispatchesForDateAsync(today);
            _logger.LogInformation("[Sync] VDR — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Vehicle dispatches error");
        }
    }

    private async Task RunLineOrderReportSyncAsync(CancellationToken ct)
    {
        try
        {
            var today = DateTime.UtcNow.Date;
            // LOR: fetch last 30 days on each daily run to catch any updates
            var from  = today.AddDays(-30);

            _logger.LogInformation("[Sync] LOR: {from} → {today}",
                from.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncLineOrderReportAsync(from, today);

            _logger.LogInformation("[Sync] LOR: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] LOR sync error");
        }
    }

    // ── Backfills (run once on startup) ──────────────────────
    private async Task RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Service history starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillHistoricalDataAsync(ct: ct);
            _logger.LogInformation("[Backfill] Service history done: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] Service history error"); }
    }

    private async Task RunVehicleSalesBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Vehicle sales starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillVehicleSalesAsync(ct: ct);
            _logger.LogInformation("[Backfill] Vehicle sales done: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] Vehicle sales error"); }
    }

    private async Task RunVehicleDispatchesBackfillAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[Backfill] Vehicle dispatches starting...");
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.BackfillVehicleDispatchesAsync(ct: ct);
            _logger.LogInformation("[Backfill] Vehicle dispatches done: {ins} inserted", r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Backfill] Vehicle dispatches error"); }
    }
}