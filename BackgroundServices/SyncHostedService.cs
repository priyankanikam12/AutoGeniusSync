using AutoGeniusSync.Services;

namespace AutoGeniusSync.BackgroundServices;

public class SyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncHostedService> _logger;

    private DateTime _lastDealerSync        = DateTime.MinValue;
    private DateTime _lastCallCentreSync    = DateTime.MinValue;
    private DateTime _lastServiceHistory    = DateTime.MinValue;
    private DateTime _lastVehicleSales      = DateTime.MinValue;
    private DateTime _lastVehicleDispatches = DateTime.MinValue;
    private DateTime _lastLineOrderReport   = DateTime.MinValue;
    private DateTime _lastReconcile         = DateTime.MinValue;
    private DateTime _lastShadowfaxRealtime = DateTime.MinValue;   // NEW

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

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            await RunDealerSyncAsync(ct);
            await RunCallCentreDealerSyncAsync(ct);

            await RunBackfillAsync(ct);
            await RunVehicleSalesBackfillAsync(ct);
            await RunVehicleDispatchesBackfillAsync(ct);

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await RunLineOrderBackfillAsync(ct);

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await RunReconcileAsync(ct, lookbackDays: 730);

            // NEW — kick off Shadowfax realtime sync immediately on startup too
            await RunShadowfaxRealtimeSyncAsync(ct);

        }, ct);

        var intervalMinutes = _config.GetValue<int>("SyncSettings:RealtimeSyncIntervalMinutes", 30);
        _logger.LogInformation("Realtime sync interval: every {min} minutes", intervalMinutes);

        var dealerIntervalMin      = _config.GetValue<int>("SyncSettings:DealerIntervalMinutes",          1440);
        var callCentreIntervalMin  = _config.GetValue<int>("SyncSettings:CallCentreIntervalMinutes",      1440);
        var serviceHistoryMin      = _config.GetValue<int>("SyncSettings:ServiceHistoryIntervalMinutes",  intervalMinutes);
        var vehicleSalesMin        = _config.GetValue<int>("SyncSettings:VehicleSalesIntervalMinutes",    intervalMinutes);
        var vehicleDispatchMin     = _config.GetValue<int>("SyncSettings:VehicleDispatchIntervalMinutes", intervalMinutes);
        var lineOrderMin           = _config.GetValue<int>("SyncSettings:LineOrderIntervalMinutes",       intervalMinutes);
        var reconcileMin           = _config.GetValue<int>("SyncSettings:ReconcileIntervalMinutes",       1440);
        var shadowfaxRealtimeMin   = _config.GetValue<int>("ShadowfaxSettings:RealtimeIntervalMinutes",   5);  // NEW

        while (!ct.IsCancellationRequested)
        {
            var now   = DateTime.UtcNow;
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

            if ((now - _lastReconcile).TotalMinutes >= reconcileMin)
            {
                _lastReconcile = now;
                tasks.Add(RunReconcileAsync(ct, lookbackDays: 90));
            }

            // NEW — fast, restricted-scope Shadowfax sync (every few minutes)
            if ((now - _lastShadowfaxRealtime).TotalMinutes >= shadowfaxRealtimeMin)
            {
                _lastShadowfaxRealtime = now;
                tasks.Add(RunShadowfaxRealtimeSyncAsync(ct));
            }

            if (tasks.Any())
                await Task.WhenAll(tasks);

            _logger.LogDebug("Sync loop sleeping 1 minute...");
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    // ── Backfills ─────────────────────────────────────────────

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

    // ── Realtime syncs ────────────────────────────────────────

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
            _logger.LogInformation(
                "[Sync] Service history — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Service history error");
        }
    }

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
            _logger.LogInformation(
                "[Sync] VSR — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
                r1.RecordsInserted, r1.RecordsUpdated, r2.RecordsInserted, r2.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Vehicle sales error");
        }
    }

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
            _logger.LogInformation(
                "[Sync] VDR — yesterday: {y_ins}ins/{y_upd}upd | today: {t_ins}ins/{t_upd}upd",
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
            var from  = today.AddDays(-30);

            _logger.LogInformation("[Sync] LOR: {from} → {today}",
                from.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncLineOrderReportAsync(from, today, ct);

            _logger.LogInformation("[Sync] LOR: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] LOR sync error");
        }
    }

    private async Task RunReconcileAsync(CancellationToken ct, int lookbackDays)
    {
        try
        {
            _logger.LogInformation("[Sync] Reconcile open jobs starting (lookback {d}d)...", lookbackDays);
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.ReconcileOpenJobsAsync(lookbackDays, ct);
            _logger.LogInformation("[Sync] Reconcile done: {upd} updated, {ins} inserted",
                r.RecordsUpdated, r.RecordsInserted);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "[Sync] Reconcile error"); }
    }

    // ── NEW: fast Shadowfax-only LOR sync ─────────────────────
    private async Task RunShadowfaxRealtimeSyncAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncShadowfaxRealtimeAsync(ct);
            _logger.LogInformation(
                "[Sync] Shadowfax realtime: {ins} inserted, {upd} updated",
                r.RecordsInserted, r.RecordsUpdated);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "[Sync] Shadowfax realtime sync error");
        }
    }
}