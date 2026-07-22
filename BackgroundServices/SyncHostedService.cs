using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.EntityFrameworkCore;

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
    private DateTime _lastShadowfaxRealtime = DateTime.MinValue;

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

            // ─────────────────────────────────────────────────
            // FIX: only run the full historical backfills if none
            // of them have EVER completed successfully before.
            // Previously this block ran unconditionally on every
            // single app restart — and the DMS_SyncLog history showed
            // dozens of overlapping, concurrent backfill runs against
            // the same date ranges, causing timeouts and contention
            // that (before the ErpApiService throw-fix) got silently
            // recorded as "Success, 0 records" instead of failures.
            // ─────────────────────────────────────────────────
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                bool serviceHistoryDone = await db.DmsSyncLogs.AnyAsync(l =>
                    l.SyncType == "BackfillHistorical" && l.Status == "Success", ct);
                bool vehicleSalesDone = await db.DmsSyncLogs.AnyAsync(l =>
                    l.SyncType == "BackfillVehicleSales" && l.Status == "Success", ct);
                bool vehicleDispatchesDone = await db.DmsSyncLogs.AnyAsync(l =>
                    l.SyncType == "BackfillVehicleDispatches" && l.Status == "Success", ct);
                bool lorDone = await db.DmsSyncLogs.AnyAsync(l =>
                    l.SyncType == "LineOrderReport" && l.Status == "Success", ct);

                // FIX: run all four historical backfills CONCURRENTLY instead of
                // sequentially — each opens its own DB scope/connection already,
                // and the pool (Max Pool Size=200) has plenty of headroom. This
                // cuts wall-clock backfill time roughly 4x since the ERP calls
                // for DJR/VSR/VDR/LOR are independent of each other.
                var backfillTasks = new List<Task>();

                if (!serviceHistoryDone)
                    backfillTasks.Add(RunBackfillAsync(ct));
                else
                    _logger.LogInformation("Service history backfill already completed previously — skipping.");

                if (!vehicleSalesDone)
                    backfillTasks.Add(RunVehicleSalesBackfillAsync(ct));
                else
                    _logger.LogInformation("Vehicle sales backfill already completed previously — skipping.");

                if (!vehicleDispatchesDone)
                    backfillTasks.Add(RunVehicleDispatchesBackfillAsync(ct));
                else
                    _logger.LogInformation("Vehicle dispatches backfill already completed previously — skipping.");

                if (!lorDone)
                    backfillTasks.Add(RunLineOrderBackfillAsync(ct));
                else
                    _logger.LogInformation("LOR backfill already completed previously — skipping.");

                if (backfillTasks.Any())
                    await Task.WhenAll(backfillTasks);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await RunReconcileAsync(ct, lookbackDays: 730);

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
        var shadowfaxRealtimeMin   = _config.GetValue<int>("ShadowfaxSettings:RealtimeIntervalMinutes",   5);

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
            _logger.LogInformation("[Sync] Service history (range): {y} → {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncServiceHistoryForRangeAsync(yesterday, today);
            _logger.LogInformation("[Sync] Service history range: {fet} fetched, {ins} inserted, {upd} updated",
                r.RecordsFetched, r.RecordsInserted, r.RecordsUpdated);
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
            _logger.LogInformation("[Sync] Vehicle sales (range): {y} → {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncVehicleSalesForRangeAsync(yesterday, today);
            _logger.LogInformation("[Sync] VSR range: {fet} fetched, {ins} inserted, {upd} updated",
                r.RecordsFetched, r.RecordsInserted, r.RecordsUpdated);
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
            _logger.LogInformation("[Sync] Vehicle dispatches (range): {y} → {t}",
                yesterday.ToShortDateString(), today.ToShortDateString());

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var r = await svc.SyncVehicleDispatchesForRangeAsync(yesterday, today);
            _logger.LogInformation("[Sync] VDR range: {fet} fetched, {ins} inserted, {upd} updated",
                r.RecordsFetched, r.RecordsInserted, r.RecordsUpdated);
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