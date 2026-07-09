//Services\DataSyncService.cs
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Services;

public class DataSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ErpApiService _erpApi;
    private readonly IConfiguration _config;
    private readonly ILogger<DataSyncService> _logger;

    public DataSyncService(
        IServiceScopeFactory scopeFactory,
        ErpApiService erpApi,
        IConfiguration config,
        ILogger<DataSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _erpApi = erpApi;
        _config = config;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC ALL DEALERS from pincode list
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncAllDealersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "Dealers", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "Dealers" };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            var dbPincodes = await db.DmsPincodeMasters
                .Where(p => p.IsActive)
                .Select(p => p.PinCode)
                .ToListAsync();

            var configPincodes = _config.GetSection("SyncSettings:Pincodes")
                                        .Get<List<string>>() ?? new();

            var pincodes = dbPincodes.Any() ? dbPincodes : configPincodes;
            pincodes = pincodes.Distinct().ToList();

            _logger.LogInformation("Syncing dealers for {count} pincodes", pincodes.Count);

            foreach (var pin in pincodes)
            {
                try
                {
                    var dealers = await _erpApi.FetchDealersByPinAsync(pin, token);
                    result.RecordsFetched += dealers.Count;

                    foreach (var d in dealers)
                    {
                        if (string.IsNullOrEmpty(d.DealerCode)) continue;

                        var existing = await db.DmsDealers
                            .FirstOrDefaultAsync(x => x.DealerCode == d.DealerCode);

                        if (existing == null)
                        {
                            db.DmsDealers.Add(MapDealer(d));
                            result.RecordsInserted++;
                        }
                        else
                        {
                            UpdateDealer(existing, d);
                            result.RecordsUpdated++;
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Pin {pin} dealer fetch failed: {msg}", pin, ex.Message);
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error = ex.Message;
            _logger.LogError(ex, "Dealer sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC SERVICE HISTORY for a single date
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncServiceHistoryForDateAsync(DateTime date)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "ServiceHistory",
            SyncDate  = DateOnly.FromDateTime(date),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "ServiceHistory", Date = date };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var jobs  = await _erpApi.FetchDjrAsync(date, token, "");
            result.RecordsFetched = jobs.Count;

            _logger.LogInformation("Date {date}: fetched {n} records",
                date.ToString("dd-MM-yyyy"), jobs.Count);

            var dedupedJobs = jobs
                .Where(j => j.JobNo != "Total")
                .Where(j => !string.IsNullOrEmpty(j.DealerCode) && !string.IsNullOrEmpty(j.JobNo))
                .GroupBy(j => $"{j.DealerCode}|{j.JobNo}")
                .Select(g => g.Last())
                .ToList();

            _logger.LogInformation("Date {date}: {raw} raw → {dedup} after dedup",
                date.ToString("dd-MM-yyyy"), jobs.Count, dedupedJobs.Count);

            foreach (var job in dedupedJobs)
            {
                try
                {
                    var parsed = ParseServiceHistory(job);

                    if (parsed.JobDate == null)
                        parsed.JobDate = DateOnly.FromDateTime(date);

                    var existing = await db.DmsServiceHistories
                        .FirstOrDefaultAsync(x =>
                            x.DealerCode == parsed.DealerCode &&
                            x.JobNo      == parsed.JobNo);

                    if (existing == null)
                    {
                        db.DmsServiceHistories.Add(parsed);
                        result.RecordsInserted++;
                    }
                    else
                    {
                        UpdateServiceHistory(existing, parsed);
                        result.RecordsUpdated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Skipping job {no} for {dc}: {msg}",
                        job.JobNo, job.DealerCode, ex.Message);
                }
            }

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error     = ex.Message;
            _logger.LogError(ex, "Service history sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // HISTORICAL BACKFILL — service history
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> BackfillHistoricalDataAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;
        var today = DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillHistorical" };
        _logger.LogInformation("Backfill: {start} → {end}",
            start.ToShortDateString(), end.ToShortDateString());

        var current = start;
        while (current <= end && !ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadySynced = current != today && await db.DmsSyncLogs.AnyAsync(l =>
                l.SyncType == "ServiceHistory" &&
                l.SyncDate == DateOnly.FromDateTime(current) &&
                l.Status   == "Success");

            if (!alreadySynced)
            {
                var r = await SyncServiceHistoryForDateAsync(current);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;
                _logger.LogInformation("Backfill {date}: +{ins} inserted, +{upd} updated",
                    current.ToShortDateString(), r.RecordsInserted, r.RecordsUpdated);
            }
            else
            {
                _logger.LogDebug("Backfill {date}: already synced, skipping",
                    current.ToShortDateString());
            }

            current = current.AddDays(1);
        }

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // RECONCILE OPEN JOBS
    //
    // WHY THIS EXISTS:
    //   RunDailyJobSyncAsync only re-syncs "today" and "yesterday".
    //   A job opened on day X but not invoiced/closed until day X+7
    //   will never get its InvoiceDate/DocType refreshed once X is
    //   more than 1 day in the past. This sweep finds every distinct
    //   JobDate that still has an open (InvoiceDate == null) job in
    //   our DB and re-pulls DJR for that specific date, so any job
    //   that has since closed on the ERP side gets updated here too.
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> ReconcileOpenJobsAsync(
        int lookbackDays = 90, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookbackDays));

        var staleDates = await db.DmsServiceHistories
            .Where(x => x.InvoiceDate == null
                     && x.JobDate != null
                     && x.JobDate >= cutoff
                     && !x.IsRowTotal)
            .Select(x => x.JobDate!.Value)
            .Distinct()
            .ToListAsync(ct);

        var totalResult = new SyncResult { SyncType = "ReconcileOpenJobs" };

        _logger.LogInformation(
            "Reconcile: {n} distinct dates have open jobs within last {d} days",
            staleDates.Count, lookbackDays);

        foreach (var date in staleDates)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var r = await SyncServiceHistoryForDateAsync(date.ToDateTime(TimeOnly.MinValue));
                totalResult.RecordsUpdated  += r.RecordsUpdated;
                totalResult.RecordsInserted += r.RecordsInserted;

                _logger.LogInformation(
                    "Reconcile {date}: +{ins} inserted, +{upd} updated",
                    date, r.RecordsInserted, r.RecordsUpdated);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reconcile failed for {date}: {msg}", date, ex.Message);
            }
        }

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC VEHICLE SALES (VSR) for a single date
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncVehicleSalesForDateAsync(DateTime date)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "VehicleSales",
            SyncDate  = DateOnly.FromDateTime(date),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleSales", Date = date };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            var dealerCodes = await db.DmsDealers
                .Where(d => d.DealerCode != null)
                .Select(d => d.DealerCode!)
                .Distinct()
                .ToListAsync();

            _logger.LogInformation("VSR sync for {date}: {n} dealers",
                date.ToString("dd-MM-yyyy"), dealerCodes.Count);

            foreach (var dealerCode in dealerCodes)
            {
                try
                {
                    var sales = await _erpApi.FetchVsrAsync(dealerCode, date, date, token);
                    result.RecordsFetched += sales.Count;

                    var dedupedSales = sales
                        .Where(s => !string.IsNullOrEmpty(s.InvoiceNo))
                        .GroupBy(s => $"{s.DealerCode}|{s.InvoiceNo}")
                        .Select(g => g.Last())
                        .ToList();

                    foreach (var sale in dedupedSales)
                    {
                        try
                        {
                            var parsed = ParseVehicleSale(sale);

                            var existing = await db.DmsVehicleSales
                                .FirstOrDefaultAsync(x =>
                                    x.DealerCode == parsed.DealerCode &&
                                    x.InvoiceNo  == parsed.InvoiceNo);

                            if (existing == null)
                            {
                                db.DmsVehicleSales.Add(parsed);
                                result.RecordsInserted++;
                            }
                            else
                            {
                                UpdateVehicleSale(existing, parsed);
                                result.RecordsUpdated++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Skipping invoice {no} for {dc}: {msg}",
                                sale.InvoiceNo, dealerCode, ex.Message);
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("VSR fetch failed for dealer {dc}: {msg}",
                        dealerCode, ex.Message);
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error     = ex.Message;
            _logger.LogError(ex, "Vehicle sales sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // HISTORICAL BACKFILL — vehicle sales
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> BackfillVehicleSalesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;
        var today = DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillVehicleSales" };
        _logger.LogInformation("VSR Backfill: {start} → {end}",
            start.ToShortDateString(), end.ToShortDateString());

        var current = start;
        while (current <= end && !ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadySynced = current != today && await db.DmsSyncLogs.AnyAsync(l =>
                l.SyncType == "VehicleSales" &&
                l.SyncDate == DateOnly.FromDateTime(current) &&
                l.Status   == "Success");

            if (!alreadySynced)
            {
                var r = await SyncVehicleSalesForDateAsync(current);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;
            }
            current = current.AddDays(1);
        }

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC CALL CENTRE DEALERS
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncCallCentreDealersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "CallCentreDealers",
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "CallCentreDealers" };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            var dbPincodes = await db.DmsPincodeMasters
                .Where(p => p.IsActive)
                .Select(p => p.PinCode)
                .ToListAsync();

            var configPincodes = _config.GetSection("SyncSettings:Pincodes")
                                        .Get<List<string>>() ?? new();

            var pincodes = dbPincodes.Any() ? dbPincodes : configPincodes;
            pincodes = pincodes.Distinct().ToList();

            _logger.LogInformation("CallCentre sync: {n} pincodes", pincodes.Count);

            foreach (var pin in pincodes)
            {
                try
                {
                    var dealers = await _erpApi.FetchCallCentreDealersByPinAsync(pin, token);
                    result.RecordsFetched += dealers.Count;

                    foreach (var d in dealers)
                    {
                        if (string.IsNullOrEmpty(d.DealerCode)) continue;

                        var existing = await db.DmsCallCentreDealers
                            .FirstOrDefaultAsync(x => x.DealerCode == d.DealerCode);

                        if (existing == null)
                        {
                            db.DmsCallCentreDealers.Add(new DmsCallCentreDealer
                            {
                                DealerCode         = d.DealerCode,
                                DealerCompany      = d.DealerCompany,
                                ContactNo          = d.ContactNo,
                                AlternateContactNo = d.AlternateContactNo,
                                DealerStateName    = d.DealerStateName,
                                PinCode            = d.PinCode ?? pin,
                                ActiveStatus       = d.ActiveStatus,
                                LastFetchedAt      = DateTime.UtcNow,
                                CreatedAt          = DateTime.UtcNow,
                                UpdatedAt          = DateTime.UtcNow
                            });
                            result.RecordsInserted++;
                        }
                        else
                        {
                            existing.DealerCompany      = d.DealerCompany;
                            existing.ContactNo          = d.ContactNo;
                            existing.AlternateContactNo = d.AlternateContactNo;
                            existing.DealerStateName    = d.DealerStateName;
                            existing.PinCode            = d.PinCode ?? pin;
                            existing.ActiveStatus       = d.ActiveStatus;
                            existing.LastFetchedAt      = DateTime.UtcNow;
                            existing.UpdatedAt          = DateTime.UtcNow;
                            result.RecordsUpdated++;
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("CallCentre pin {pin} failed: {msg}", pin, ex.Message);
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error     = ex.Message;
            _logger.LogError(ex, "CallCentre dealer sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC VEHICLE DISPATCHES for a single date
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncVehicleDispatchesForDateAsync(DateTime date)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "VehicleDispatches",
            SyncDate  = DateOnly.FromDateTime(date),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleDispatches", Date = date };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            var dispatches = await _erpApi.FetchVdrAsync(date, date, token);
            result.RecordsFetched = dispatches.Count;

            _logger.LogInformation("VDR {date}: fetched {n} records",
                date.ToString("dd-MM-yyyy"), dispatches.Count);

            var deduped = dispatches
                .Where(d => !string.IsNullOrEmpty(d.InvoiceNo))
                .GroupBy(d => $"{d.InvoiceNo}|{d.ChassisNo}")
                .Select(g => g.Last())
                .ToList();

            foreach (var d in deduped)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);

                    var existing = await db.DmsVehicleDispatches
                        .FirstOrDefaultAsync(x =>
                            x.InvoiceNo == parsed.InvoiceNo &&
                            x.ChassisNo == parsed.ChassisNo);

                    if (existing == null)
                    {
                        db.DmsVehicleDispatches.Add(parsed);
                        result.RecordsInserted++;
                    }
                    else
                    {
                        UpdateVehicleDispatch(existing, parsed);
                        result.RecordsUpdated++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Skipping dispatch {inv}/{ch}: {msg}",
                        d.InvoiceNo, d.ChassisNo, ex.Message);
                }
            }

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error     = ex.Message;
            _logger.LogError(ex, "Vehicle dispatches sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // HISTORICAL BACKFILL — vehicle dispatches
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> BackfillVehicleDispatchesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;
        var today = DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillVehicleDispatches" };

        var current = start;
        while (current <= end && !ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadySynced = current != today && await db.DmsSyncLogs.AnyAsync(l =>
                l.SyncType == "VehicleDispatches" &&
                l.SyncDate == DateOnly.FromDateTime(current) &&
                l.Status   == "Success");

            if (!alreadySynced)
            {
                var r = await SyncVehicleDispatchesForDateAsync(current);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;
            }

            current = current.AddDays(1);
        }

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // SYNC LINE ORDER REPORT (LOR)
    //
    // WHY THIS IS RANGE-BASED (not day-by-day like DJR):
    //   The LOR API requires a dealercode per request.
    //   It ignores single-day ranges and returns all records within
    //   the date window. Day-by-day calls return 0 for almost every
    //   dealer on any single day. We must pass a wide date range.
    //
    // FIX: removed ActiveStatus filter — DMS_Dealers may store
    //   "Active", "1", "active" or NULL depending on pincode API.
    //   Filtering by ActiveStatus silently returned 0 dealers.
    //   Now we take ALL dealers with a non-null DealerCode.
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> SyncLineOrderReportAsync(
        DateTime startDate, DateTime endDate,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "LineOrderReport",
            SyncDate  = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "LineOrderReport", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            // ── FIX: NO ActiveStatus filter ──────────────────────────
            // The pincode API (booking/pin) stores ActiveStatus as "Active"
            // but this is inconsistent across dealers.
            // Take ALL dealers regardless of status — same as VSR sync.
            var dealerCodes = await db.DmsDealers
                .Where(d => d.DealerCode != null)
                .Select(d => d.DealerCode!)
                .Distinct()
                .ToListAsync();

            if (!dealerCodes.Any())
            {
                _logger.LogWarning(
                    "LOR sync: DMS_Dealers is empty. Run POST /api/sync/dealers first.");
                log.Status       = "Failed";
                log.ErrorMessage = "No dealers in DMS_Dealers. Run dealer sync first.";
                log.CompletedAt  = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return result;
            }

            _logger.LogInformation(
                "LOR sync {from} → {to}: {n} dealers",
                startDate.ToString("dd-MM-yyyy"),
                endDate.ToString("dd-MM-yyyy"),
                dealerCodes.Count);

            foreach (var dealerCode in dealerCodes)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(200, ct);
                try
                {
                    var records = await _erpApi.FetchLorAsync(
                        dealerCode, startDate, endDate, token);

                    result.RecordsFetched += records.Count;

                    var deduped = records
                        .Where(r => !string.IsNullOrEmpty(r.UniqueId)
                                && !string.IsNullOrEmpty(r.DealerCode))
                        .GroupBy(r => $"{r.DealerCode}|{r.UniqueId}")
                        .Select(g => g.Last())
                        .ToList();

                    if (!deduped.Any()) continue;

                    // ── BULK LOOKUP: one query per dealer instead of one per record ──
                    var uniqueIds = deduped
                        .Select(r => r.UniqueId!)
                        .ToList();

                    var existingRecords = await db.DmsLineOrderReports
                        .Where(x => x.DealerCode == dealerCode
                                && uniqueIds.Contains(x.UniqueId!))
                        .ToDictionaryAsync(x => x.UniqueId!, x => x);

                    foreach (var rec in deduped)
                    {
                        try
                        {
                            var parsed = MapLineOrderReport(rec);

                            if (existingRecords.TryGetValue(parsed.UniqueId!, out var existing))
                            {
                                UpdateLineOrderReport(existing, parsed);
                                result.RecordsUpdated++;
                            }
                            else
                            {
                                db.DmsLineOrderReports.Add(parsed);
                                result.RecordsInserted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                "Skipping LOR UniqueId {id} for dealer {dc}: {msg}",
                                rec.UniqueId, dealerCode, ex.Message);
                        }
                    }

                    await db.SaveChangesAsync(); // one save per dealer, not per record
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("LOR fetch failed for dealer {dc}: {msg}",
                        dealerCode, ex.Message);
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error     = ex.Message;
            _logger.LogError(ex, "LOR sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // Thin wrapper — keeps SyncHostedService compatible
    public Task<SyncResult> SyncLineOrderReportForDateAsync(DateTime date)
        => SyncLineOrderReportAsync(date, date);

    // Full historical backfill — one wide range call per dealer
    public async Task<SyncResult> BackfillLineOrderReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        _logger.LogInformation("LOR Backfill: {start} → {end}",
            start.ToShortDateString(), end.ToShortDateString());

        return await SyncLineOrderReportAsync(start, end, ct);
    }

    // ─────────────────────────────────────────────────────────
    // MAPPING HELPERS
    // ─────────────────────────────────────────────────────────

    private static DmsDealer MapDealer(DealerValue d) => new()
    {
        DealerCode         = d.DealerCode,
        DealerCompany      = d.DealerCompany,
        ContactNo          = d.ContactNo,
        AlternateContactNo = d.AlternateContactNo,
        DealerStateName    = d.DealerStateName,
        DealerCityName     = d.DealerCityName,
        PinCode            = d.PinCode,
        ActiveStatus       = d.ActiveStatus,
        LastFetchedAt      = DateTime.UtcNow,
        CreatedAt          = DateTime.UtcNow,
        UpdatedAt          = DateTime.UtcNow
    };

    private static void UpdateDealer(DmsDealer e, DealerValue d)
    {
        e.DealerCompany      = d.DealerCompany;
        e.ContactNo          = d.ContactNo;
        e.AlternateContactNo = d.AlternateContactNo;
        e.DealerStateName    = d.DealerStateName;
        e.DealerCityName     = d.DealerCityName;
        e.PinCode            = d.PinCode;
        e.ActiveStatus       = d.ActiveStatus;
        e.LastFetchedAt      = DateTime.UtcNow;
        e.UpdatedAt          = DateTime.UtcNow;
    }

    private static DmsServiceHistory ParseServiceHistory(DjrValue j)
    {
        return new DmsServiceHistory
        {
            DealerCode           = j.DealerCode,
            JobNo                = j.JobNo,
            JobDate              = ParseDate(j.JobDate),
            CompName             = j.CompName,
            Location             = j.Location,
            InTime               = j.InTime,
            CloseTime            = j.CloseTime,
            JobCategory          = j.JobCategory,
            Ffrpercentage        = j.FFRPercentage,
            DocNo                = j.DocNo,
            DocType              = j.DocType,
            DocDate              = ParseDate(j.DocDate),
            Model                = j.Model,
            BrandName            = j.BrandName,
            RegNo                = j.RegNo,
            VehicleType          = j.VehicleType,
            EngineNo             = j.EngineNo,
            ChassisNo            = j.ChassisNo,
            Kms                  = j.KMS,
            BatterySerialNo1     = j.BatterySerialNo1,
            BatterySerialNo2     = j.BatterySerialNo2,
            BatterySerialNo3     = j.BatterySerialNo3,
            BatterySerialNo4     = j.BatterySerialNo4,
            BatterySerialNo5     = j.BatterySerialNo5,
            BatterySerialNo6     = j.BatterySerialNo6,
            IndividualAhbattery1 = j.IndividualAHBattery1,
            IndividualAhbattery2 = j.IndividualAHBattery2,
            IndividualAhbattery3 = j.IndividualAHBattery3,
            IndividualAhbattery4 = j.IndividualAHBattery4,
            IndividualAhbattery5 = j.IndividualAHBattery5,
            IndividualAhbattery6 = j.IndividualAHBattery6,
            PartyName            = j.PartyName,
            MobileNumber         = j.MobileNumber,
            Supervisor           = j.Supervisor,
            Technician           = j.Technician,
            ServiceHead          = j.ServiceHead,
            JobType              = j.JobType,
            SaleDate             = ParseDate(j.SaleDate),
            CouponNo             = j.CouponNo,
            ExpectedDeliveryDate = ParseDate(j.ExpectedDeliveryDate),
            ProformaDate         = ParseDate(j.ProformaDate),
            InvoiceDate          = ParseDate(j.InvoiceDate),
            EstimatedJobExpenses = ParseDecimal(j.EstimatedJobExpenses),
            LabourHours          = ParseDecimal(j.LabourHours),
            Parts                = ParseDecimal(j.Parts),
            Accessory            = ParseDecimal(j.Accessory),
            Oil                  = ParseDecimal(j.Oil),
            Labour               = ParseDecimal(j.Labour),
            OutsideWork          = ParseDecimal(j.OutsideWork),
            TotalWotax           = ParseDecimal(j.TotalWOTax),
            Gstamount            = ParseDecimal(j.GSTAmount),
            Igstamount           = ParseDecimal(j.IGSTAmount),
            NetTotal             = ParseDecimal(j.NetTotal),
            IsRowTotal           = j.JobNo == "Total",
            CreatedAt            = DateTime.UtcNow,
            UpdatedAt            = DateTime.UtcNow
        };
    }

    private static void UpdateServiceHistory(DmsServiceHistory e, DmsServiceHistory n)
    {
        e.JobDate              = n.JobDate;
        e.CompName             = n.CompName;
        e.Location             = n.Location;
        e.InTime               = n.InTime;
        e.CloseTime            = n.CloseTime;
        e.JobCategory          = n.JobCategory;
        e.Ffrpercentage        = n.Ffrpercentage;
        e.DocNo                = n.DocNo;
        e.DocType              = n.DocType;
        e.DocDate              = n.DocDate;
        e.Model                = n.Model;
        e.BrandName            = n.BrandName;
        e.RegNo                = n.RegNo;
        e.VehicleType          = n.VehicleType;
        e.EngineNo             = n.EngineNo;
        e.ChassisNo            = n.ChassisNo;
        e.Kms                  = n.Kms;
        e.BatterySerialNo1     = n.BatterySerialNo1;
        e.BatterySerialNo2     = n.BatterySerialNo2;
        e.BatterySerialNo3     = n.BatterySerialNo3;
        e.BatterySerialNo4     = n.BatterySerialNo4;
        e.BatterySerialNo5     = n.BatterySerialNo5;
        e.BatterySerialNo6     = n.BatterySerialNo6;
        e.IndividualAhbattery1 = n.IndividualAhbattery1;
        e.IndividualAhbattery2 = n.IndividualAhbattery2;
        e.IndividualAhbattery3 = n.IndividualAhbattery3;
        e.IndividualAhbattery4 = n.IndividualAhbattery4;
        e.IndividualAhbattery5 = n.IndividualAhbattery5;
        e.IndividualAhbattery6 = n.IndividualAhbattery6;
        e.PartyName            = n.PartyName;
        e.MobileNumber         = n.MobileNumber;
        e.Supervisor           = n.Supervisor;
        e.Technician           = n.Technician;
        e.ServiceHead          = n.ServiceHead;
        e.JobType              = n.JobType;
        e.SaleDate             = n.SaleDate;
        e.CouponNo             = n.CouponNo;
        e.ExpectedDeliveryDate = n.ExpectedDeliveryDate;
        e.ProformaDate         = n.ProformaDate;
        e.InvoiceDate          = n.InvoiceDate;
        e.EstimatedJobExpenses = n.EstimatedJobExpenses;
        e.LabourHours          = n.LabourHours;
        e.Parts                = n.Parts;
        e.Accessory            = n.Accessory;
        e.Oil                  = n.Oil;
        e.Labour               = n.Labour;
        e.OutsideWork          = n.OutsideWork;
        e.TotalWotax           = n.TotalWotax;
        e.Gstamount            = n.Gstamount;
        e.Igstamount           = n.Igstamount;
        e.NetTotal             = n.NetTotal;
        e.UpdatedAt            = DateTime.UtcNow;
    }

    private static DmsVehicleSale ParseVehicleSale(VsrValue v) => new()
    {
        DealerName        = v.DealerName,
        DealerCode        = v.DealerCode,
        InvoiceNo         = v.InvoiceNo,
        InvoiceDate       = ParseDate(v.InvoiceDate),
        Location          = v.Location,
        LocCode           = v.LocCode,
        LocationCity      = v.LocationCity,
        CustDob           = ParseDate(v.CustDOB),
        Gender            = v.Gender,
        SoldTo            = v.SoldTo,
        AccountType       = v.AccountType,
        PartyEmail        = v.PartyEmail,
        CusMob            = v.CusMob,
        Address1          = v.Address1,
        Address2          = v.Address2,
        City              = v.City,
        State             = v.State,
        ExecutiveName     = v.ExecutiveName,
        Pin               = v.Pin,
        ChassisNo         = v.ChassisNo,
        MotorNo           = v.MotorNo,
        Remarks           = v.Remarks,
        ItemModel         = v.ItemModel,
        Oemmodel          = v.OEMModel,
        ColorCode         = v.ColorCode,
        VehicleType       = v.VehicleType,
        VehicleGroup      = v.VehicleGroup,
        Hsnsaccode        = v.HSNSACCode,
        SaleType          = v.SaleType,
        FinancedBy        = v.FinancedBy,
        FinAmount         = ParseDecimal(v.FinAmount),
        ItemRate          = ParseDecimal(v.ItemRate),
        InsuAmount        = ParseDecimal(v.InsuAmount),
        RegnAmount        = ParseDecimal(v.RegnAmount),
        AcsryAmount       = ParseDecimal(v.AcsryAmount),
        PreGstdiscAmount  = ParseDecimal(v.PreGSTDiscAmount),
        DiscTypeName      = v.DiscTypeName,
        PostGstdisc       = ParseDecimal(v.PostGSTDisc),
        FameIi            = ParseDecimal(v.FameII),
        StateFameIi       = ParseDecimal(v.StateFameII),
        Sgstper           = ParseDecimal(v.SGSTPer),
        Sgstamount        = ParseDecimal(v.SGSTAmount),
        Cgstper           = ParseDecimal(v.CGSTPer),
        Cgstamount        = ParseDecimal(v.CGSTAmount),
        Igstper           = ParseDecimal(v.IGSTPer),
        Igstamount        = ParseDecimal(v.IGSTAmount),
        NetAmount         = ParseDecimal(v.NetAmount),
        ReferenceNo       = v.ReferenceNo,
        BookingDate       = ParseDate(v.BookingDate),
        TotalCount        = v.TotalCount,
        Battery           = v.Battery,
        BatteryChemical   = v.BatteryChemical,
        BatteryCapacity   = v.BatteryCapacity,
        BatteryMake       = v.BatteryMake,
        ChargerNo         = v.ChargerNo,
        ChargerNo2        = v.ChargerNo2,
        Converter         = v.Converter,
        Vcu               = v.VCU,
        ControllerNo      = v.ControllerNo,
        FameIirequired    = v.FameIIRequired,
        SegmentName       = v.SegmentName,
        InstitutionalName = v.InstitutionalName,
        SchemeName        = v.SchemeName,
        CreatedAt         = DateTime.UtcNow,
        UpdatedAt         = DateTime.UtcNow
    };

    private static void UpdateVehicleSale(DmsVehicleSale e, DmsVehicleSale n)
    {
        e.DealerName = n.DealerName; e.InvoiceDate = n.InvoiceDate;
        e.Location = n.Location; e.LocCode = n.LocCode; e.LocationCity = n.LocationCity;
        e.CustDob = n.CustDob; e.Gender = n.Gender; e.SoldTo = n.SoldTo;
        e.AccountType = n.AccountType; e.PartyEmail = n.PartyEmail; e.CusMob = n.CusMob;
        e.Address1 = n.Address1; e.Address2 = n.Address2; e.City = n.City;
        e.State = n.State; e.ExecutiveName = n.ExecutiveName; e.Pin = n.Pin;
        e.ChassisNo = n.ChassisNo; e.MotorNo = n.MotorNo; e.Remarks = n.Remarks;
        e.ItemModel = n.ItemModel; e.Oemmodel = n.Oemmodel; e.ColorCode = n.ColorCode;
        e.VehicleType = n.VehicleType; e.VehicleGroup = n.VehicleGroup;
        e.Hsnsaccode = n.Hsnsaccode; e.SaleType = n.SaleType; e.FinancedBy = n.FinancedBy;
        e.FinAmount = n.FinAmount; e.ItemRate = n.ItemRate; e.InsuAmount = n.InsuAmount;
        e.RegnAmount = n.RegnAmount; e.AcsryAmount = n.AcsryAmount;
        e.PreGstdiscAmount = n.PreGstdiscAmount; e.DiscTypeName = n.DiscTypeName;
        e.PostGstdisc = n.PostGstdisc; e.FameIi = n.FameIi; e.StateFameIi = n.StateFameIi;
        e.Sgstper = n.Sgstper; e.Sgstamount = n.Sgstamount;
        e.Cgstper = n.Cgstper; e.Cgstamount = n.Cgstamount;
        e.Igstper = n.Igstper; e.Igstamount = n.Igstamount; e.NetAmount = n.NetAmount;
        e.ReferenceNo = n.ReferenceNo; e.BookingDate = n.BookingDate;
        e.TotalCount = n.TotalCount; e.Battery = n.Battery;
        e.BatteryChemical = n.BatteryChemical; e.BatteryCapacity = n.BatteryCapacity;
        e.BatteryMake = n.BatteryMake; e.ChargerNo = n.ChargerNo;
        e.ChargerNo2 = n.ChargerNo2; e.Converter = n.Converter;
        e.Vcu = n.Vcu; e.ControllerNo = n.ControllerNo;
        e.FameIirequired = n.FameIirequired; e.SegmentName = n.SegmentName;
        e.InstitutionalName = n.InstitutionalName; e.SchemeName = n.SchemeName;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsVehicleDispatch MapVehicleDispatch(VdrValue d) => new()
    {
        SaleDate = ParseDate(d.SaleDate), InvoiceNo = d.InvoiceNo,
        InvoiceDate = ParseDate(d.InvoiceDate), Location = d.Location,
        LocationCode = d.LocationCode, LocationCity = d.LocationCity,
        LocationStatus = d.LocationStatus, DealerName = d.DealerName,
        Zone = d.Zone, AreaOffice = d.AreaOffice, MfgYear = d.MfgYear,
        BrandName = d.BrandName, ModelCode = d.ModelCode, ColorCode = d.ColorCode,
        ChassisNo = d.ChassisNo, RegNo = d.RegNo, MotorNo = d.MotorNo,
        BatteryId = d.BatteryId, BatteryNo = d.BatteryNo,
        EcuSerialNo = d.EcuSerialNo, EcuImEi = d.EcuImEi, EcuBalMac = d.EcuBalMac,
        ImmoblizerNo = d.ImmoblizerNo, BikeSimId = d.BikeSimId,
        BikeMobileNo = d.BikeMobileNo, ChargerNo = d.ChargerNo,
        ControllerNo = d.ControllerNo, SoundbarSerialNo = d.SoundbarSerialNo,
        SoundbarBalMac = d.SoundbarBalMac, Voltage = d.Voltage,
        RegNumber = d.RegNumber, StartDate = ParseDate(d.StartDate),
        Tyre1 = d.Tyre1, Tyre2 = d.Tyre2, VehicleStatus = d.VehicleStatus,
        BookingId = d.BookingId, BillNo = d.BillNo,
        BillDate = ParseDate(d.BillDate), BillType = d.BillType,
        FinancerName = d.FinancerName, FinAmount = ParseDecimal(d.FinAmount),
        NameOfParty = d.NameOfParty, Address1 = d.Address1, Address2 = d.Address2,
        State = d.State, City = d.City, Pin = d.Pin, MobileNo = d.MobileNo,
        Email = d.Email, AppPush = d.AppPush, LeadId = d.LeadId, Vcu = d.Vcu,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateVehicleDispatch(DmsVehicleDispatch e, DmsVehicleDispatch n)
    {
        e.SaleDate = n.SaleDate; e.InvoiceDate = n.InvoiceDate;
        e.Location = n.Location; e.LocationCode = n.LocationCode;
        e.LocationCity = n.LocationCity; e.LocationStatus = n.LocationStatus;
        e.DealerName = n.DealerName; e.Zone = n.Zone; e.AreaOffice = n.AreaOffice;
        e.MfgYear = n.MfgYear; e.BrandName = n.BrandName; e.ModelCode = n.ModelCode;
        e.ColorCode = n.ColorCode; e.RegNo = n.RegNo; e.MotorNo = n.MotorNo;
        e.BatteryId = n.BatteryId; e.BatteryNo = n.BatteryNo;
        e.EcuSerialNo = n.EcuSerialNo; e.EcuImEi = n.EcuImEi; e.EcuBalMac = n.EcuBalMac;
        e.ImmoblizerNo = n.ImmoblizerNo; e.BikeSimId = n.BikeSimId;
        e.BikeMobileNo = n.BikeMobileNo; e.ChargerNo = n.ChargerNo;
        e.ControllerNo = n.ControllerNo; e.SoundbarSerialNo = n.SoundbarSerialNo;
        e.SoundbarBalMac = n.SoundbarBalMac; e.Voltage = n.Voltage;
        e.RegNumber = n.RegNumber; e.StartDate = n.StartDate;
        e.Tyre1 = n.Tyre1; e.Tyre2 = n.Tyre2; e.VehicleStatus = n.VehicleStatus;
        e.BookingId = n.BookingId; e.BillNo = n.BillNo; e.BillDate = n.BillDate;
        e.BillType = n.BillType; e.FinancerName = n.FinancerName; e.FinAmount = n.FinAmount;
        e.NameOfParty = n.NameOfParty; e.Address1 = n.Address1; e.Address2 = n.Address2;
        e.State = n.State; e.City = n.City; e.Pin = n.Pin; e.MobileNo = n.MobileNo;
        e.Email = n.Email; e.AppPush = n.AppPush; e.LeadId = n.LeadId; e.Vcu = n.Vcu;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsLineOrderReport MapLineOrderReport(LorValue r) => new()
    {
        DealerName      = r.DealerName,
        DealerCode      = r.DealerCode,
        UniqueId        = r.UniqueId,
        LocCode         = r.LocCode,
        DocDate         = ParseDate(r.DocDate),
        DocNo           = r.DocNo,
        DocType         = r.DocType,
        JobDate         = ParseDate(r.JobDate),
        JobNo           = r.JobNo,
        BrandName       = r.BrandName,
        Model           = r.Model,
        JobCardType     = r.JobCardType,
        PaymentMode     = r.PaymentMode,
        PartyName       = r.PartyName,
        PartyMobile     = r.PartyMobile,
        RegNo           = r.RegNo,
        VehicleType     = r.VehicleType,
        ChassisNo       = r.ChassisNo,
        Location        = r.Location,
        ItemName        = r.ItemName,
        ItemDescription = r.ItemDescription,
        ItemType        = r.ItemType,
        Qty             = r.Qty,
        Rate            = ParseDecimal(r.Rate),
        Total           = ParseDecimal(r.Total),
        SgstPer         = ParseDecimal(r.SgstPer),
        SgstAmount      = ParseDecimal(r.SgstAmount),
        CgstPer         = ParseDecimal(r.CgstPer),
        CgstAmount      = ParseDecimal(r.CgstAmount),
        IgstPer         = ParseDecimal(r.IgstPer),
        IgstAmount      = ParseDecimal(r.IgstAmount),
        Discount        = ParseDecimal(r.Discount),
        TotalAmount     = ParseDecimal(r.TotalAmount),
        Mrp             = ParseDecimal(r.Mrp),
        DealerType      = r.DealerType,
        CreatedAt       = DateTime.UtcNow,
        UpdatedAt       = DateTime.UtcNow
    };

    private static void UpdateLineOrderReport(DmsLineOrderReport e, DmsLineOrderReport n)
    {
        e.DocDate = n.DocDate; e.DocNo = n.DocNo; e.DocType = n.DocType;
        e.JobDate = n.JobDate; e.JobNo = n.JobNo;
        e.BrandName = n.BrandName; e.Model = n.Model;
        e.JobCardType = n.JobCardType; e.PaymentMode = n.PaymentMode;
        e.PartyName = n.PartyName; e.PartyMobile = n.PartyMobile;
        e.RegNo = n.RegNo; e.VehicleType = n.VehicleType;
        e.ChassisNo = n.ChassisNo; e.Location = n.Location;
        e.ItemName = n.ItemName; e.ItemDescription = n.ItemDescription;
        e.ItemType = n.ItemType; e.Qty = n.Qty;
        e.Rate = n.Rate; e.Total = n.Total;
        e.SgstPer = n.SgstPer; e.SgstAmount = n.SgstAmount;
        e.CgstPer = n.CgstPer; e.CgstAmount = n.CgstAmount;
        e.IgstPer = n.IgstPer; e.IgstAmount = n.IgstAmount;
        e.Discount = n.Discount; e.TotalAmount = n.TotalAmount;
        e.Mrp = n.Mrp; e.DealerType = n.DealerType;
        e.UpdatedAt = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────
    // SHARED PARSE HELPERS
    // ─────────────────────────────────────────────────────────

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        if (DateTime.TryParseExact(val, "dd-MM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d))
            return DateOnly.FromDateTime(d);
        if (DateTime.TryParse(val, out var d2))
            return DateOnly.FromDateTime(d2);
        return null;
    }

    private static decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        return decimal.TryParse(val,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : 0;
    }
}