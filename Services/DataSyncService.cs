using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Services;

public class DataSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ErpApiService _erpApi;
    private readonly IConfiguration _config;
    private readonly ILogger<DataSyncService> _logger;

    private const int SaveBatchSize = 500;

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

    private List<string> GetConfiguredShadowfaxDealerCodes()
        => _config.GetSection("ShadowfaxSettings:DealerCodes").Get<List<string>>() ?? new();

    // Used ONLY by ServiceHistory (unchanged)
    private static string BuildUniqueKey(string? dealerCode, string? jobNo, DateOnly? date, string? chassisNo)
        => $"{dealerCode?.Trim().ToUpperInvariant()}{jobNo?.Trim().ToUpperInvariant()}{date?.ToString("yyyy-MM-dd")}{chassisNo?.Trim().ToUpperInvariant()}";

    // FIX: LOR's own UniqueId field is unreliable — the ERP reuses the
    // same UniqueId across genuinely different line items. Uniqueness is
    // now determined by this 6-field composite instead: DealerCode + JobNo
    // + JobDate + ChassisNo + ItemName + ItemDescription. UniqueId is still
    // stored on the row (for reference/debugging) but is no longer used
    // for matching or dedup.
    private static string BuildLorUniqueKey(
        string? dealerCode, string? jobNo, DateOnly? jobDate,
        string? chassisNo, string? itemName, string? itemDescription)
        => $"{dealerCode?.Trim().ToUpperInvariant()}" +
           $"{jobNo?.Trim().ToUpperInvariant()}" +
           $"{jobDate?.ToString("yyyy-MM-dd")}" +
           $"{chassisNo?.Trim().ToUpperInvariant()}" +
           $"{itemName?.Trim().ToUpperInvariant()}" +
           $"{itemDescription?.Trim().ToUpperInvariant()}";

    private static Dictionary<string, T> BuildLookupTolerant<T>(List<T> source, Func<T, string?> keySelector)
        => source
            .Where(x => keySelector(x) != null)
            .GroupBy(x => keySelector(x)!)
            .ToDictionary(g => g.Key, g => g.First());

    // ─────────────────────────────────────────────────────────
    // SYNC DEALERS DIRECTLY FROM BaplFinal.C_CustomerMaster
    // ─────────────────────────────────────────────────────────
    public async Task<SyncResult> SyncDealersFromBaplAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "DealersFromBapl", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "DealersFromBapl" };
        int skippedOnError = 0;

        try
        {
            var baplConnStr = _config.GetConnectionString("BaplConnection")
                ?? throw new Exception("BaplConnection connection string is not configured.");

            using var conn = new SqlConnection(baplConnStr);
            await conn.OpenAsync();

            using var cmd = new SqlCommand("SELECT * FROM C_CustomerMaster", conn)
            {
                CommandTimeout = 120
            };

            using var reader = await cmd.ExecuteReaderAsync();

            var existingDealers = await db.DmsDealers.ToListAsync();
            var existingLookup = existingDealers
                .Where(d => d.DealerCode != null)
                .GroupBy(d => d.DealerCode!)
                .ToDictionary(g => g.Key, g => g.First());

            int saveCounter = 0;

            while (await reader.ReadAsync())
            {
                try
                {
                    result.RecordsFetched++;

                    string? GetStr(string col) =>
                        HasColumn(reader, col) && reader[col] != DBNull.Value ? reader[col].ToString() : null;

                    var dealerCode = GetStr("DealerCode") ?? GetStr("CustomerCode") ?? GetStr("Code");
                    if (string.IsNullOrEmpty(dealerCode))
                    {
                        skippedOnError++;
                        continue;
                    }

                    var dealerCompany   = GetStr("CustomerName") ?? GetStr("Name") ?? GetStr("DealerName");
                    var pinCode         = GetStr("PinCode") ?? GetStr("Pincode") ?? GetStr("Pin");
                    var contactNo       = GetStr("ContactNo") ?? GetStr("MobileNo") ?? GetStr("Mobile");
                    var activeStatusRaw = GetStr("ActiveStatus") ?? GetStr("IsActive") ?? GetStr("Status");
                    var stateName       = GetStr("StateName") ?? GetStr("State");
                    var cityName        = GetStr("CityName") ?? GetStr("City");

                    var activeStatus = activeStatusRaw?.Trim().ToUpperInvariant() == "Y" ? "Active" : "Inactive";

                    if (existingLookup.TryGetValue(dealerCode, out var existingDealer))
                    {
                        existingDealer.DealerCompany   = dealerCompany;
                        existingDealer.ContactNo       = contactNo;
                        existingDealer.PinCode         = pinCode;
                        existingDealer.DealerStateName = stateName;
                        existingDealer.DealerCityName  = cityName;
                        existingDealer.ActiveStatus    = activeStatus;
                        existingDealer.LastFetchedAt   = DateTime.UtcNow;
                        existingDealer.UpdatedAt       = DateTime.UtcNow;
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsDealers.Add(new DmsDealer
                        {
                            DealerCode         = dealerCode,
                            DealerCompany      = dealerCompany,
                            ContactNo          = contactNo,
                            PinCode            = pinCode,
                            DealerStateName    = stateName,
                            DealerCityName     = cityName,
                            ActiveStatus       = activeStatus,
                            LastFetchedAt      = DateTime.UtcNow,
                            CreatedAt          = DateTime.UtcNow,
                            UpdatedAt          = DateTime.UtcNow
                        });
                        result.RecordsInserted++;
                    }

                    saveCounter++;
                    if (saveCounter % SaveBatchSize == 0)
                        await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping BAPL dealer row: {msg}", ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedOnError} row(s) skipped due to per-record errors.";
            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "BAPL dealer sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    private static bool HasColumn(SqlDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════
    // DEALERS — unchanged
    // ═══════════════════════════════════════════════════════

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

            int processed = 0;

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

                processed++;
                if (processed % 100 == 0 || processed == pincodes.Count)
                {
                    log.RecordsFetched  = result.RecordsFetched;
                    log.RecordsInserted = result.RecordsInserted;
                    log.RecordsUpdated  = result.RecordsUpdated;
                    await db.SaveChangesAsync();
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "Dealer sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ═══════════════════════════════════════════════════════
    // SERVICE HISTORY — unchanged
    // ═══════════════════════════════════════════════════════

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

            var droppedAsTotal = jobs.Count(j => j.JobNo == "Total");
            var afterTotalFilter = jobs.Where(j => j.JobNo != "Total").ToList();

            var droppedAsBlankKey = afterTotalFilter.Count(j =>
                string.IsNullOrEmpty(j.DealerCode) || string.IsNullOrEmpty(j.JobNo));
            var afterBlankFilter = afterTotalFilter
                .Where(j => !string.IsNullOrEmpty(j.DealerCode) && !string.IsNullOrEmpty(j.JobNo))
                .ToList();

            var parsedRecords = afterBlankFilter
                .Select(j =>
                {
                    var parsed = ParseServiceHistory(j);
                    if (parsed.JobDate == null)
                        parsed.JobDate = DateOnly.FromDateTime(date);
                    parsed.UniqueKey = BuildUniqueKey(parsed.DealerCode, parsed.JobNo, parsed.JobDate, parsed.ChassisNo);
                    return parsed;
                })
                .ToList();

            var dedupedRecords = parsedRecords
                .GroupBy(p => p.UniqueKey)
                .Select(g => g.Last())
                .ToList();
            var droppedAsDuplicateInBatch = parsedRecords.Count - dedupedRecords.Count;

            var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
            var existingRaw = await db.DmsServiceHistories
                .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                .ToListAsync();
            var existingLookup = BuildLookupTolerant(existingRaw, x => x.UniqueKey);

            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var parsed in dedupedRecords)
            {
                try
                {
                    if (existingLookup.TryGetValue(parsed.UniqueKey!, out var existing))
                    {
                        UpdateServiceHistory(existing, parsed);
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsServiceHistories.Add(parsed);
                        result.RecordsInserted++;
                    }

                    saveCounter++;
                    if (saveCounter % SaveBatchSize == 0)
                        await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping job {no} for {dc}: {msg}",
                        parsed.JobNo, parsed.DealerCode, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsTotal} Total-row, {droppedAsBlankKey} blank-key, " +
                $"{droppedAsDuplicateInBatch} in-batch duplicate, {skippedOnError} per-record error(s).";

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "Service history sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public async Task<SyncResult> SyncServiceHistoryForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "ServiceHistory",
            SyncDate  = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "ServiceHistory", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var jobs = await _erpApi.FetchDjrRangeAsync(startDate, endDate, token);
            result.RecordsFetched = jobs.Count;

            var droppedAsTotal = jobs.Count(j => j.JobNo == "Total");
            var afterTotalFilter = jobs.Where(j => j.JobNo != "Total").ToList();

            var droppedAsBlankKey = afterTotalFilter.Count(j =>
                string.IsNullOrEmpty(j.DealerCode) || string.IsNullOrEmpty(j.JobNo));
            var afterBlankFilter = afterTotalFilter
                .Where(j => !string.IsNullOrEmpty(j.DealerCode) && !string.IsNullOrEmpty(j.JobNo))
                .ToList();

            var parsedRecords = afterBlankFilter
                .Select(j =>
                {
                    var parsed = ParseServiceHistory(j);
                    if (parsed.JobDate == null)
                        parsed.JobDate = DateOnly.FromDateTime(endDate);
                    parsed.UniqueKey = BuildUniqueKey(parsed.DealerCode, parsed.JobNo, parsed.JobDate, parsed.ChassisNo);
                    return parsed;
                })
                .ToList();

            var dedupedRecords = parsedRecords
                .GroupBy(p => p.UniqueKey)
                .Select(g => g.Last())
                .ToList();
            var droppedAsDuplicateInBatch = parsedRecords.Count - dedupedRecords.Count;

            _logger.LogInformation(
                "DJR range {from} → {to}: {raw} raw → {dropTotal} dropped (Total) → " +
                "{dropBlank} dropped (blank key) → {dropDup} dropped (in-batch dup) → {final} to process",
                startDate.ToString("dd-MM-yyyy"), endDate.ToString("dd-MM-yyyy"),
                jobs.Count, droppedAsTotal, droppedAsBlankKey, droppedAsDuplicateInBatch, dedupedRecords.Count);

            var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
            var existingRaw = await db.DmsServiceHistories
                .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                .ToListAsync();
            var existingLookup = BuildLookupTolerant(existingRaw, x => x.UniqueKey);

            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var parsed in dedupedRecords)
            {
                try
                {
                    if (existingLookup.TryGetValue(parsed.UniqueKey!, out var existing))
                    {
                        UpdateServiceHistory(existing, parsed);
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsServiceHistories.Add(parsed);
                        result.RecordsInserted++;
                    }

                    saveCounter++;
                    if (saveCounter % SaveBatchSize == 0)
                        await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping job {no} for {dc}: {msg}", parsed.JobNo, parsed.DealerCode, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsTotal} Total-row, {droppedAsBlankKey} blank-key, " +
                $"{droppedAsDuplicateInBatch} in-batch duplicate, {skippedOnError} per-record error(s).";

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "DJR range sync failed for {from} → {to}",
                startDate.ToShortDateString(), endDate.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public async Task<SyncResult> BackfillHistoricalDataAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var topLog = new DmsSyncLog { SyncType = "BackfillHistorical", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(topLog);
        await db.SaveChangesAsync();

        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillHistorical" };

        try
        {
            var chunkStart = start;
            while (chunkStart <= end && !ct.IsCancellationRequested)
            {
                var chunkEnd = chunkStart.AddMonths(1).AddDays(-1);
                if (chunkEnd > end) chunkEnd = end;

                var r = await SyncServiceHistoryForRangeAsync(chunkStart, chunkEnd);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;

                chunkStart = chunkEnd.AddDays(1);
            }

            topLog.Status = "Success";
        }
        catch (Exception ex)
        {
            topLog.Status = "Failed";
            topLog.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            totalResult.Error = topLog.ErrorMessage;
            _logger.LogError(ex, "Historical service history backfill failed");
        }

        topLog.CompletedAt = DateTime.UtcNow;
        topLog.RecordsFetched  = totalResult.RecordsFetched;
        topLog.RecordsInserted = totalResult.RecordsInserted;
        topLog.RecordsUpdated  = totalResult.RecordsUpdated;
        await db.SaveChangesAsync();

        return totalResult;
    }

    public async Task<SyncResult> ReconcileOpenJobsAsync(
        int lookbackDays = 90, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookbackDays));

        var staleDates = await db.DmsServiceHistories
            .Where(x => x.InvoiceDate == null && x.JobDate != null && x.JobDate >= cutoff && !x.IsRowTotal)
            .Select(x => x.JobDate!.Value)
            .Distinct()
            .ToListAsync(ct);

        var totalResult = new SyncResult { SyncType = "ReconcileOpenJobs" };

        foreach (var date in staleDates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await SyncServiceHistoryForDateAsync(date.ToDateTime(TimeOnly.MinValue));
                totalResult.RecordsUpdated  += r.RecordsUpdated;
                totalResult.RecordsInserted += r.RecordsInserted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reconcile failed for {date}: {msg}", date, ex.Message);
            }
        }

        return totalResult;
    }

    // ═══════════════════════════════════════════════════════
    // VEHICLE SALES (VSR) — unchanged, keyed by ChassisNo
    // ═══════════════════════════════════════════════════════

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

            int droppedAsBlankTotal = 0;
            int droppedAsDuplicateTotal = 0;
            int skippedOnErrorTotal = 0;
            int saveCounter = 0;

            foreach (var dealerCode in dealerCodes)
            {
                try
                {
                    var sales = await _erpApi.FetchVsrAsync(dealerCode, date, date, token);
                    result.RecordsFetched += sales.Count;

                    var droppedAsBlank = sales.Count(s => string.IsNullOrEmpty(s.ChassisNo));
                    droppedAsBlankTotal += droppedAsBlank;

                    var afterBlankFilter = sales
                        .Where(s => !string.IsNullOrEmpty(s.ChassisNo))
                        .ToList();

                    var dedupedRecords = afterBlankFilter
                        .GroupBy(s => s.ChassisNo!.Trim().ToUpperInvariant())
                        .Select(g => g.Last())
                        .ToList();
                    droppedAsDuplicateTotal += afterBlankFilter.Count - dedupedRecords.Count;

                    var chassisKeys = dedupedRecords.Select(s => s.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
                    var existingRaw = await db.DmsVehicleSales
                        .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                        .ToListAsync();
                    var existingLookup = BuildLookupTolerant(existingRaw, x => x.ChassisNo?.Trim().ToUpperInvariant());

                    foreach (var sale in dedupedRecords)
                    {
                        try
                        {
                            var parsed = ParseVehicleSale(sale);
                            var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                            if (existingLookup.TryGetValue(key, out var existing))
                            {
                                UpdateVehicleSale(existing, parsed);
                                result.RecordsUpdated++;
                            }
                            else
                            {
                                db.DmsVehicleSales.Add(parsed);
                                result.RecordsInserted++;
                            }

                            saveCounter++;
                            if (saveCounter % SaveBatchSize == 0)
                                await db.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            skippedOnErrorTotal++;
                            _logger.LogWarning("Skipping sale (chassis {ch}) for {dc}: {msg}",
                                sale.ChassisNo, dealerCode, ex.Message);
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("VSR fetch failed for dealer {dc}: {msg}", dealerCode, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsBlankTotal} blank-chassis, {droppedAsDuplicateTotal} in-batch duplicate, " +
                $"{skippedOnErrorTotal} per-record error(s). Keyed by ChassisNo only.";

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "Vehicle sales sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public async Task<SyncResult> SyncVehicleSalesForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "VehicleSales",
            SyncDate  = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleSales", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var sales = await _erpApi.FetchVsrAsync("", startDate, endDate, token);
            result.RecordsFetched = sales.Count;

            var droppedAsBlank = sales.Count(s => string.IsNullOrEmpty(s.ChassisNo));
            var afterBlankFilter = sales
                .Where(s => !string.IsNullOrEmpty(s.ChassisNo))
                .ToList();

            var dedupedRecords = afterBlankFilter
                .GroupBy(s => s.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();
            var droppedAsDuplicate = afterBlankFilter.Count - dedupedRecords.Count;

            _logger.LogInformation(
                "VSR range {from} → {to}: {raw} raw → {dropBlank} dropped (blank chassis) → " +
                "{dropDup} dropped (in-batch dup, keyed by ChassisNo) → {final} to process",
                startDate.ToString("dd-MM-yyyy"), endDate.ToString("dd-MM-yyyy"),
                sales.Count, droppedAsBlank, droppedAsDuplicate, dedupedRecords.Count);

            var chassisKeys = dedupedRecords.Select(s => s.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingLookup = new Dictionary<string, DmsVehicleSale>();
            foreach (var chunk in chassisKeys.Chunk(500))
            {
                var chunkSet = chunk.ToHashSet();
                var rows = await db.DmsVehicleSales
                    .Where(x => x.ChassisNo != null && chunkSet.Contains(x.ChassisNo!.Trim().ToUpper()))
                    .AsNoTracking()
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var key = row.ChassisNo?.Trim().ToUpperInvariant();
                    if (key != null && !existingLookup.ContainsKey(key))
                        existingLookup[key] = row;
                }
            }

            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var sale in dedupedRecords)
            {
                try
                {
                    var parsed = ParseVehicleSale(sale);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingLookup.TryGetValue(key, out var existing))
                    {
                        db.Attach(existing);
                        UpdateVehicleSale(existing, parsed);
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsVehicleSales.Add(parsed);
                        result.RecordsInserted++;
                    }

                    saveCounter++;
                    if (saveCounter % SaveBatchSize == 0)
                        await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping sale (chassis {ch}): {msg}", sale.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsBlank} blank-chassis, {droppedAsDuplicate} in-batch duplicate, " +
                $"{skippedOnError} per-record error(s). Keyed by ChassisNo only.";

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "VSR range sync failed for {from} → {to}",
                startDate.ToShortDateString(), endDate.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public async Task<SyncResult> BackfillVehicleSalesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var topLog = new DmsSyncLog { SyncType = "BackfillVehicleSales", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(topLog);
        await db.SaveChangesAsync();

        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillVehicleSales" };

        try
        {
            var chunkStart = start;
            while (chunkStart <= end && !ct.IsCancellationRequested)
            {
                var chunkEnd = chunkStart.AddMonths(1).AddDays(-1);
                if (chunkEnd > end) chunkEnd = end;

                var r = await SyncVehicleSalesForRangeAsync(chunkStart, chunkEnd);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;

                chunkStart = chunkEnd.AddDays(1);
            }

            topLog.Status = "Success";
        }
        catch (Exception ex)
        {
            topLog.Status = "Failed";
            topLog.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            totalResult.Error = topLog.ErrorMessage;
            _logger.LogError(ex, "VSR historical backfill failed");
        }

        topLog.CompletedAt = DateTime.UtcNow;
        topLog.RecordsFetched  = totalResult.RecordsFetched;
        topLog.RecordsInserted = totalResult.RecordsInserted;
        topLog.RecordsUpdated  = totalResult.RecordsUpdated;
        await db.SaveChangesAsync();

        return totalResult;
    }

    // ═══════════════════════════════════════════════════════
    // CALL CENTRE DEALERS — unchanged
    // ═══════════════════════════════════════════════════════

    public async Task<SyncResult> SyncCallCentreDealersAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "CallCentreDealers", StartedAt = DateTime.UtcNow, Status = "Running" };
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
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "CallCentre dealer sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ═══════════════════════════════════════════════════════
    // VEHICLE DISPATCHES (VDR) — unchanged, keyed by ChassisNo
    // ═══════════════════════════════════════════════════════

    public async Task<SyncResult> SyncVehicleDispatchesForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "VehicleDispatches",
            SyncDate  = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleDispatches", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var dispatches = await _erpApi.FetchVdrAsync(startDate, endDate, token);
            result.RecordsFetched = dispatches.Count;

            var droppedAsBlank = dispatches.Count(d => string.IsNullOrEmpty(d.ChassisNo));
            var afterBlankFilter = dispatches
                .Where(d => !string.IsNullOrEmpty(d.ChassisNo))
                .ToList();

            var dedupedRecords = afterBlankFilter
                .GroupBy(d => d.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();
            var droppedAsDuplicate = afterBlankFilter.Count - dedupedRecords.Count;

            _logger.LogInformation(
                "VDR range {from} → {to}: {raw} raw → {dropBlank} dropped (blank chassis) → " +
                "{dropDup} dropped (in-batch dup, keyed by ChassisNo) → {final} to process",
                startDate.ToString("dd-MM-yyyy"), endDate.ToString("dd-MM-yyyy"),
                dispatches.Count, droppedAsBlank, droppedAsDuplicate, dedupedRecords.Count);

            var chassisKeys = dedupedRecords.Select(d => d.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingLookup = new Dictionary<string, DmsVehicleDispatch>();
            foreach (var chunk in chassisKeys.Chunk(500))
            {
                var chunkSet = chunk.ToHashSet();
                var rows = await db.DmsVehicleDispatches
                    .Where(x => x.ChassisNo != null && chunkSet.Contains(x.ChassisNo!.Trim().ToUpper()))
                    .AsNoTracking()
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var key = row.ChassisNo?.Trim().ToUpperInvariant();
                    if (key != null && !existingLookup.ContainsKey(key))
                        existingLookup[key] = row;
                }
            }

            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var d in dedupedRecords)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingLookup.TryGetValue(key, out var existing))
                    {
                        db.Attach(existing);
                        UpdateVehicleDispatch(existing, parsed);
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsVehicleDispatches.Add(parsed);
                        result.RecordsInserted++;
                    }

                    saveCounter++;
                    if (saveCounter % SaveBatchSize == 0)
                        await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping dispatch (chassis {ch}): {msg}", d.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsBlank} blank-chassis, {droppedAsDuplicate} in-batch duplicate, " +
                $"{skippedOnError} per-record error(s). Keyed by ChassisNo only.";

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "VDR range sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

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

            var droppedAsBlank = dispatches.Count(d => string.IsNullOrEmpty(d.ChassisNo));
            var afterBlankFilter = dispatches
                .Where(d => !string.IsNullOrEmpty(d.ChassisNo))
                .ToList();

            var dedupedRecords = afterBlankFilter
                .GroupBy(d => d.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();
            var droppedAsDuplicate = afterBlankFilter.Count - dedupedRecords.Count;

            var chassisKeys = dedupedRecords.Select(d => d.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingRaw = await db.DmsVehicleDispatches
                .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                .ToListAsync();
            var existingLookup = BuildLookupTolerant(existingRaw, x => x.ChassisNo?.Trim().ToUpperInvariant());

            int skippedOnError = 0;

            foreach (var d in dedupedRecords)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingLookup.TryGetValue(key, out var existing))
                    {
                        UpdateVehicleDispatch(existing, parsed);
                        result.RecordsUpdated++;
                    }
                    else
                    {
                        db.DmsVehicleDispatches.Add(parsed);
                        result.RecordsInserted++;
                    }
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping dispatch (chassis {ch}): {msg}",
                        d.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsBlank} blank-chassis, {droppedAsDuplicate} in-batch duplicate, " +
                $"{skippedOnError} per-record error(s). Keyed by ChassisNo only.";

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "Vehicle dispatches sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public async Task<SyncResult> BackfillVehicleDispatchesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var topLog = new DmsSyncLog { SyncType = "BackfillVehicleDispatches", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(topLog);
        await db.SaveChangesAsync();

        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillVehicleDispatches" };

        try
        {
            var chunkStart = start;
            while (chunkStart <= end && !ct.IsCancellationRequested)
            {
                var chunkEnd = chunkStart.AddMonths(1).AddDays(-1);
                if (chunkEnd > end) chunkEnd = end;

                var r = await SyncVehicleDispatchesForRangeAsync(chunkStart, chunkEnd);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;

                chunkStart = chunkEnd.AddDays(1);
            }

            topLog.Status = "Success";
        }
        catch (Exception ex)
        {
            topLog.Status = "Failed";
            topLog.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            totalResult.Error = topLog.ErrorMessage;
            _logger.LogError(ex, "VDR historical backfill failed");
        }

        topLog.CompletedAt = DateTime.UtcNow;
        topLog.RecordsFetched  = totalResult.RecordsFetched;
        topLog.RecordsInserted = totalResult.RecordsInserted;
        topLog.RecordsUpdated  = totalResult.RecordsUpdated;
        await db.SaveChangesAsync();

        return totalResult;
    }

    // ═══════════════════════════════════════════════════════
    // LINE ORDER REPORT (LOR) — CHANGED: matching/dedup key is now
    // the 6-field composite (DealerCode+JobNo+JobDate+ChassisNo+
    // ItemName+ItemDescription), NOT UniqueId. UniqueId itself is
    // unreliable — the ERP reuses the same UniqueId across genuinely
    // different line items.
    // ═══════════════════════════════════════════════════════

    public async Task<SyncResult> SyncLineOrderReportAsync(
        DateTime startDate, DateTime endDate,
        CancellationToken ct = default,
        List<string>? dealerCodesOverride = null)
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

            List<string> dealerCodes;

            if (dealerCodesOverride != null && dealerCodesOverride.Any())
            {
                dealerCodes = dealerCodesOverride.Distinct().ToList();
            }
            else
            {
                dealerCodes = await db.DmsDealers
                    .Where(d => d.DealerCode != null)
                    .Select(d => d.DealerCode!)
                    .Distinct()
                    .ToListAsync();

                if (!dealerCodes.Any())
                {
                    log.Status       = "Failed";
                    log.ErrorMessage = "No dealers in DMS_Dealers. Run dealer sync first.";
                    log.CompletedAt  = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return result;
                }
            }

            int droppedAsBlankTotal = 0;
            int droppedAsDuplicateTotal = 0;
            int skippedOnErrorTotal = 0;
            int saveCounter = 0;

            foreach (var dealerCode in dealerCodes)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(200, ct);
                try
                {
                    var records = await _erpApi.FetchLorAsync(dealerCode, startDate, endDate, token);
                    result.RecordsFetched += records.Count;

                    var parsedRecords = records
                        .Select(r =>
                        {
                            var parsed = MapLineOrderReport(r);
                            parsed.UniqueKey = BuildLorUniqueKey(
                                parsed.DealerCode, parsed.JobNo, parsed.JobDate,
                                parsed.ChassisNo, parsed.ItemName, parsed.ItemDescription);
                            return parsed;
                        })
                        .ToList();

                    // FIX: no longer dropping records that are missing one or more
                    // of the 6 key fields. BuildLorUniqueKey already handles nulls
                    // by substituting empty string for that segment — a record
                    // with a blank ItemDescription (e.g. a labour/service charge
                    // with no part description) is still real data and gets kept.
                    // We only track how many records have a partial key, for
                    // visibility — this no longer removes them.
                    var partialKeyCount = parsedRecords.Count(p =>
                        string.IsNullOrEmpty(p.DealerCode) || string.IsNullOrEmpty(p.JobNo) ||
                        p.JobDate == null || string.IsNullOrEmpty(p.ChassisNo) ||
                        string.IsNullOrEmpty(p.ItemName) || string.IsNullOrEmpty(p.ItemDescription));
                    droppedAsBlankTotal += 0; // no longer dropping; kept for log clarity below

                    // Only genuinely unusable rows (every single field blank —
                    // meaning the ERP sent essentially nothing) are excluded, since
                    // there's nothing to key or store meaningfully.
                    var afterBlankFilter = parsedRecords
                        .Where(p =>
                            !string.IsNullOrEmpty(p.DealerCode) || !string.IsNullOrEmpty(p.JobNo) ||
                            p.JobDate != null || !string.IsNullOrEmpty(p.ChassisNo) ||
                            !string.IsNullOrEmpty(p.ItemName) || !string.IsNullOrEmpty(p.ItemDescription))
                        .ToList();
                    var droppedAsCompletelyEmpty = parsedRecords.Count - afterBlankFilter.Count;

                    var dedupedRecords = afterBlankFilter
                        .GroupBy(p => p.UniqueKey)
                        .Select(g => g.Last())
                        .ToList();
                    droppedAsDuplicateTotal += afterBlankFilter.Count - dedupedRecords.Count;

                    if (!dedupedRecords.Any()) continue;

                    var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
                    var existingRaw = await db.DmsLineOrderReports
                        .Where(x => x.DealerCode == dealerCode && x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                        .ToListAsync();
                    var existingLookup = BuildLookupTolerant(existingRaw, x => x.UniqueKey);

                    foreach (var parsed in dedupedRecords)
                    {
                        try
                        {
                            if (existingLookup.TryGetValue(parsed.UniqueKey!, out var existing))
                            {
                                UpdateLineOrderReport(existing, parsed);
                                result.RecordsUpdated++;
                            }
                            else
                            {
                                db.DmsLineOrderReports.Add(parsed);
                                result.RecordsInserted++;
                            }

                            saveCounter++;
                            if (saveCounter % SaveBatchSize == 0)
                                await db.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            if (ex.InnerException?.Message.Contains("UX_LineOrderReport_UniqueKey") == true)
                            {
                                _logger.LogInformation(
                                    "Concurrent insert detected for job {jn}, item {it} — already added by another sync run, skipping.",
                                    parsed.JobNo, parsed.ItemName);
                            }
                            else
                            {
                                skippedOnErrorTotal++;
                                _logger.LogWarning(
                                    "Skipping LOR item (dealer {dc}, job {jn}, item {it}): {msg}",
                                    dealerCode, parsed.JobNo, parsed.ItemName, ex.Message);
                            }
                        }
                    }

                    await db.SaveChangesAsync();

                    _logger.LogInformation(
                        "LOR dealer {dc}: {total} parsed, {partial} with a partial key (kept), {empty} completely empty (dropped), {dup} in-batch duplicate",
                        dealerCode, parsedRecords.Count, partialKeyCount, droppedAsCompletelyEmpty, afterBlankFilter.Count - dedupedRecords.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("LOR fetch failed for dealer {dc}: {msg}", dealerCode, ex.Message);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsBlankTotal} completely-empty-record, {droppedAsDuplicateTotal} in-batch duplicate, " +
                $"{skippedOnErrorTotal} per-record error(s). Keyed by DealerCode+JobNo+JobDate+ChassisNo+ItemName+ItemDescription " +
                $"(partial keys allowed — a record only needs ONE non-blank field among the six to be kept).";
            result.Error = log.ErrorMessage;

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error     = log.ErrorMessage;
            _logger.LogError(ex, "LOR sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    public Task<SyncResult> SyncLineOrderReportForDateAsync(DateTime date)
        => SyncLineOrderReportAsync(date, date);

    public async Task<SyncResult> SyncShadowfaxRealtimeAsync(CancellationToken ct = default)
    {
        var shadowfaxCodes = GetConfiguredShadowfaxDealerCodes();
        if (!shadowfaxCodes.Any())
            return new SyncResult { SyncType = "ShadowfaxRealtime", Error = "No Shadowfax dealer codes configured." };

        var lookbackDays = _config.GetValue<int>("ShadowfaxSettings:LookbackDays", 3);
        var today = DateTime.UtcNow.Date;
        var from  = today.AddDays(-lookbackDays);

        return await SyncLineOrderReportAsync(from, today, ct, dealerCodesOverride: shadowfaxCodes);
    }

    public async Task<SyncResult> BackfillLineOrderReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        return await SyncLineOrderReportAsync(start, end, ct);
    }

    // ═══════════════════════════════════════════════════════
    // MAPPING HELPERS — unchanged
    // ═══════════════════════════════════════════════════════

    private static DmsDealer MapDealer(DealerValue d) => new()
    {
        DealerCode = d.DealerCode, DealerCompany = d.DealerCompany, ContactNo = d.ContactNo,
        AlternateContactNo = d.AlternateContactNo, DealerStateName = d.DealerStateName,
        DealerCityName = d.DealerCityName, PinCode = d.PinCode, ActiveStatus = d.ActiveStatus,
        LastFetchedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateDealer(DmsDealer e, DealerValue d)
    {
        e.DealerCompany = d.DealerCompany; e.ContactNo = d.ContactNo;
        e.AlternateContactNo = d.AlternateContactNo; e.DealerStateName = d.DealerStateName;
        e.DealerCityName = d.DealerCityName; e.PinCode = d.PinCode; e.ActiveStatus = d.ActiveStatus;
        e.LastFetchedAt = DateTime.UtcNow; e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsServiceHistory ParseServiceHistory(DjrValue j) => new()
    {
        DealerCode = j.DealerCode, JobNo = j.JobNo, JobDate = ParseDate(j.JobDate),
        CompName = j.CompName, Location = j.Location, InTime = j.InTime, CloseTime = j.CloseTime,
        JobCategory = j.JobCategory, Ffrpercentage = j.FFRPercentage, DocNo = j.DocNo, DocType = j.DocType,
        DocDate = ParseDate(j.DocDate), Model = j.Model, BrandName = j.BrandName, RegNo = j.RegNo,
        VehicleType = j.VehicleType, EngineNo = j.EngineNo, ChassisNo = j.ChassisNo, Kms = j.KMS,
        BatterySerialNo1 = j.BatterySerialNo1, BatterySerialNo2 = j.BatterySerialNo2,
        BatterySerialNo3 = j.BatterySerialNo3, BatterySerialNo4 = j.BatterySerialNo4,
        BatterySerialNo5 = j.BatterySerialNo5, BatterySerialNo6 = j.BatterySerialNo6,
        IndividualAhbattery1 = j.IndividualAHBattery1, IndividualAhbattery2 = j.IndividualAHBattery2,
        IndividualAhbattery3 = j.IndividualAHBattery3, IndividualAhbattery4 = j.IndividualAHBattery4,
        IndividualAhbattery5 = j.IndividualAHBattery5, IndividualAhbattery6 = j.IndividualAHBattery6,
        PartyName = j.PartyName, MobileNumber = j.MobileNumber, Supervisor = j.Supervisor,
        Technician = j.Technician, ServiceHead = j.ServiceHead, JobType = j.JobType,
        SaleDate = ParseDate(j.SaleDate), CouponNo = j.CouponNo,
        ExpectedDeliveryDate = ParseDate(j.ExpectedDeliveryDate), ProformaDate = ParseDate(j.ProformaDate),
        InvoiceDate = ParseDate(j.InvoiceDate), EstimatedJobExpenses = ParseDecimal(j.EstimatedJobExpenses),
        LabourHours = ParseDecimal(j.LabourHours), Parts = ParseDecimal(j.Parts),
        Accessory = ParseDecimal(j.Accessory), Oil = ParseDecimal(j.Oil), Labour = ParseDecimal(j.Labour),
        OutsideWork = ParseDecimal(j.OutsideWork), TotalWotax = ParseDecimal(j.TotalWOTax),
        Gstamount = ParseDecimal(j.GSTAmount), Igstamount = ParseDecimal(j.IGSTAmount),
        NetTotal = ParseDecimal(j.NetTotal), IsRowTotal = j.JobNo == "Total",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateServiceHistory(DmsServiceHistory e, DmsServiceHistory n)
    {
        e.JobDate = n.JobDate; e.CompName = n.CompName; e.Location = n.Location; e.InTime = n.InTime;
        e.CloseTime = n.CloseTime; e.JobCategory = n.JobCategory; e.Ffrpercentage = n.Ffrpercentage;
        e.DocNo = n.DocNo; e.DocType = n.DocType; e.DocDate = n.DocDate; e.Model = n.Model;
        e.BrandName = n.BrandName; e.RegNo = n.RegNo; e.VehicleType = n.VehicleType; e.EngineNo = n.EngineNo;
        e.ChassisNo = n.ChassisNo; e.Kms = n.Kms; e.BatterySerialNo1 = n.BatterySerialNo1;
        e.BatterySerialNo2 = n.BatterySerialNo2; e.BatterySerialNo3 = n.BatterySerialNo3;
        e.BatterySerialNo4 = n.BatterySerialNo4; e.BatterySerialNo5 = n.BatterySerialNo5;
        e.BatterySerialNo6 = n.BatterySerialNo6; e.IndividualAhbattery1 = n.IndividualAhbattery1;
        e.IndividualAhbattery2 = n.IndividualAhbattery2; e.IndividualAhbattery3 = n.IndividualAhbattery3;
        e.IndividualAhbattery4 = n.IndividualAhbattery4; e.IndividualAhbattery5 = n.IndividualAhbattery5;
        e.IndividualAhbattery6 = n.IndividualAhbattery6; e.PartyName = n.PartyName;
        e.MobileNumber = n.MobileNumber; e.Supervisor = n.Supervisor; e.Technician = n.Technician;
        e.ServiceHead = n.ServiceHead; e.JobType = n.JobType; e.SaleDate = n.SaleDate;
        e.CouponNo = n.CouponNo; e.ExpectedDeliveryDate = n.ExpectedDeliveryDate;
        e.ProformaDate = n.ProformaDate; e.InvoiceDate = n.InvoiceDate;
        e.EstimatedJobExpenses = n.EstimatedJobExpenses; e.LabourHours = n.LabourHours;
        e.Parts = n.Parts; e.Accessory = n.Accessory; e.Oil = n.Oil; e.Labour = n.Labour;
        e.OutsideWork = n.OutsideWork; e.TotalWotax = n.TotalWotax; e.Gstamount = n.Gstamount;
        e.Igstamount = n.Igstamount; e.NetTotal = n.NetTotal; e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsVehicleSale ParseVehicleSale(VsrValue v) => new()
    {
        DealerName = v.DealerName, DealerCode = v.DealerCode, InvoiceNo = v.InvoiceNo,
        InvoiceDate = ParseDate(v.InvoiceDate), Location = v.Location, LocCode = v.LocCode,
        LocationCity = v.LocationCity, CustDob = ParseDate(v.CustDOB), Gender = v.Gender,
        SoldTo = v.SoldTo, AccountType = v.AccountType, PartyEmail = v.PartyEmail, CusMob = v.CusMob,
        Address1 = v.Address1, Address2 = v.Address2, City = v.City, State = v.State,
        ExecutiveName = v.ExecutiveName, Pin = v.Pin, ChassisNo = v.ChassisNo, MotorNo = v.MotorNo,
        Remarks = v.Remarks, ItemModel = v.ItemModel, Oemmodel = v.OEMModel, ColorCode = v.ColorCode,
        VehicleType = v.VehicleType, VehicleGroup = v.VehicleGroup, Hsnsaccode = v.HSNSACCode,
        SaleType = v.SaleType, FinancedBy = v.FinancedBy, FinAmount = ParseDecimal(v.FinAmount),
        ItemRate = ParseDecimal(v.ItemRate), InsuAmount = ParseDecimal(v.InsuAmount),
        RegnAmount = ParseDecimal(v.RegnAmount), AcsryAmount = ParseDecimal(v.AcsryAmount),
        PreGstdiscAmount = ParseDecimal(v.PreGSTDiscAmount), DiscTypeName = v.DiscTypeName,
        PostGstdisc = ParseDecimal(v.PostGSTDisc), FameIi = ParseDecimal(v.FameII),
        StateFameIi = ParseDecimal(v.StateFameII), Sgstper = ParseDecimal(v.SGSTPer),
        Sgstamount = ParseDecimal(v.SGSTAmount), Cgstper = ParseDecimal(v.CGSTPer),
        Cgstamount = ParseDecimal(v.CGSTAmount), Igstper = ParseDecimal(v.IGSTPer),
        Igstamount = ParseDecimal(v.IGSTAmount), NetAmount = ParseDecimal(v.NetAmount),
        ReferenceNo = v.ReferenceNo, BookingDate = ParseDate(v.BookingDate), TotalCount = v.TotalCount,
        Battery = v.Battery, BatteryChemical = v.BatteryChemical, BatteryCapacity = v.BatteryCapacity,
        BatteryMake = v.BatteryMake, ChargerNo = v.ChargerNo, ChargerNo2 = v.ChargerNo2,
        Converter = v.Converter, Vcu = v.VCU, ControllerNo = v.ControllerNo,
        FameIirequired = v.FameIIRequired, SegmentName = v.SegmentName,
        InstitutionalName = v.InstitutionalName, SchemeName = v.SchemeName,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateVehicleSale(DmsVehicleSale e, DmsVehicleSale n)
    {
        e.DealerName = n.DealerName; e.DealerCode = n.DealerCode; e.InvoiceNo = n.InvoiceNo;
        e.InvoiceDate = n.InvoiceDate; e.Location = n.Location;
        e.LocCode = n.LocCode; e.LocationCity = n.LocationCity; e.CustDob = n.CustDob;
        e.Gender = n.Gender; e.SoldTo = n.SoldTo; e.AccountType = n.AccountType;
        e.PartyEmail = n.PartyEmail; e.CusMob = n.CusMob; e.Address1 = n.Address1;
        e.Address2 = n.Address2; e.City = n.City; e.State = n.State; e.ExecutiveName = n.ExecutiveName;
        e.Pin = n.Pin; e.MotorNo = n.MotorNo; e.Remarks = n.Remarks;
        e.ItemModel = n.ItemModel; e.Oemmodel = n.Oemmodel; e.ColorCode = n.ColorCode;
        e.VehicleType = n.VehicleType; e.VehicleGroup = n.VehicleGroup; e.Hsnsaccode = n.Hsnsaccode;
        e.SaleType = n.SaleType; e.FinancedBy = n.FinancedBy; e.FinAmount = n.FinAmount;
        e.ItemRate = n.ItemRate; e.InsuAmount = n.InsuAmount; e.RegnAmount = n.RegnAmount;
        e.AcsryAmount = n.AcsryAmount; e.PreGstdiscAmount = n.PreGstdiscAmount;
        e.DiscTypeName = n.DiscTypeName; e.PostGstdisc = n.PostGstdisc; e.FameIi = n.FameIi;
        e.StateFameIi = n.StateFameIi; e.Sgstper = n.Sgstper; e.Sgstamount = n.Sgstamount;
        e.Cgstper = n.Cgstper; e.Cgstamount = n.Cgstamount; e.Igstper = n.Igstper;
        e.Igstamount = n.Igstamount; e.NetAmount = n.NetAmount; e.ReferenceNo = n.ReferenceNo;
        e.BookingDate = n.BookingDate; e.TotalCount = n.TotalCount; e.Battery = n.Battery;
        e.BatteryChemical = n.BatteryChemical; e.BatteryCapacity = n.BatteryCapacity;
        e.BatteryMake = n.BatteryMake; e.ChargerNo = n.ChargerNo; e.ChargerNo2 = n.ChargerNo2;
        e.Converter = n.Converter; e.Vcu = n.Vcu; e.ControllerNo = n.ControllerNo;
        e.FameIirequired = n.FameIirequired; e.SegmentName = n.SegmentName;
        e.InstitutionalName = n.InstitutionalName; e.SchemeName = n.SchemeName;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsVehicleDispatch MapVehicleDispatch(VdrValue d) => new()
    {
        SaleDate = ParseDate(d.SaleDate), InvoiceNo = d.InvoiceNo, InvoiceDate = ParseDate(d.InvoiceDate),
        Location = d.Location, LocationCode = d.LocationCode, LocationCity = d.LocationCity,
        LocationStatus = d.LocationStatus, DealerName = d.DealerName, Zone = d.Zone,
        AreaOffice = d.AreaOffice, MfgYear = d.MfgYear, BrandName = d.BrandName, ModelCode = d.ModelCode,
        ColorCode = d.ColorCode, ChassisNo = d.ChassisNo, RegNo = d.RegNo, MotorNo = d.MotorNo,
        BatteryId = d.BatteryId, BatteryNo = d.BatteryNo, EcuSerialNo = d.EcuSerialNo,
        EcuImEi = d.EcuImEi, EcuBalMac = d.EcuBalMac, ImmoblizerNo = d.ImmoblizerNo,
        BikeSimId = d.BikeSimId, BikeMobileNo = d.BikeMobileNo, ChargerNo = d.ChargerNo,
        ControllerNo = d.ControllerNo, SoundbarSerialNo = d.SoundbarSerialNo,
        SoundbarBalMac = d.SoundbarBalMac, Voltage = d.Voltage, RegNumber = d.RegNumber,
        StartDate = ParseDate(d.StartDate), Tyre1 = d.Tyre1, Tyre2 = d.Tyre2,
        VehicleStatus = d.VehicleStatus, BookingId = d.BookingId, BillNo = d.BillNo,
        BillDate = ParseDate(d.BillDate), BillType = d.BillType, FinancerName = d.FinancerName,
        FinAmount = ParseDecimal(d.FinAmount), NameOfParty = d.NameOfParty, Address1 = d.Address1,
        Address2 = d.Address2, State = d.State, City = d.City, Pin = d.Pin, MobileNo = d.MobileNo,
        Email = d.Email, AppPush = d.AppPush, LeadId = d.LeadId, Vcu = d.Vcu,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateVehicleDispatch(DmsVehicleDispatch e, DmsVehicleDispatch n)
    {
        e.SaleDate = n.SaleDate; e.InvoiceNo = n.InvoiceNo; e.InvoiceDate = n.InvoiceDate; e.Location = n.Location;
        e.LocationCode = n.LocationCode; e.LocationCity = n.LocationCity; e.LocationStatus = n.LocationStatus;
        e.DealerName = n.DealerName; e.Zone = n.Zone; e.AreaOffice = n.AreaOffice; e.MfgYear = n.MfgYear;
        e.BrandName = n.BrandName; e.ModelCode = n.ModelCode; e.ColorCode = n.ColorCode;
        e.RegNo = n.RegNo; e.MotorNo = n.MotorNo; e.BatteryId = n.BatteryId; e.BatteryNo = n.BatteryNo;
        e.EcuSerialNo = n.EcuSerialNo; e.EcuImEi = n.EcuImEi; e.EcuBalMac = n.EcuBalMac;
        e.ImmoblizerNo = n.ImmoblizerNo; e.BikeSimId = n.BikeSimId; e.BikeMobileNo = n.BikeMobileNo;
        e.ChargerNo = n.ChargerNo; e.ControllerNo = n.ControllerNo; e.SoundbarSerialNo = n.SoundbarSerialNo;
        e.SoundbarBalMac = n.SoundbarBalMac; e.Voltage = n.Voltage; e.RegNumber = n.RegNumber;
        e.StartDate = n.StartDate; e.Tyre1 = n.Tyre1; e.Tyre2 = n.Tyre2; e.VehicleStatus = n.VehicleStatus;
        e.BookingId = n.BookingId; e.BillNo = n.BillNo; e.BillDate = n.BillDate; e.BillType = n.BillType;
        e.FinancerName = n.FinancerName; e.FinAmount = n.FinAmount; e.NameOfParty = n.NameOfParty;
        e.Address1 = n.Address1; e.Address2 = n.Address2; e.State = n.State; e.City = n.City;
        e.Pin = n.Pin; e.MobileNo = n.MobileNo; e.Email = n.Email; e.AppPush = n.AppPush;
        e.LeadId = n.LeadId; e.Vcu = n.Vcu; e.UpdatedAt = DateTime.UtcNow;
    }

    private static DmsLineOrderReport MapLineOrderReport(LorValue r) => new()
    {
        DealerName = r.DealerName, DealerCode = r.DealerCode, UniqueId = r.UniqueId, LocCode = r.LocCode,
        DocDate = ParseDate(r.DocDate), DocNo = r.DocNo, DocType = r.DocType, JobDate = ParseDate(r.JobDate),
        JobNo = r.JobNo, BrandName = r.BrandName, Model = r.Model, JobCardType = r.JobCardType,
        PaymentMode = r.PaymentMode, PartyName = r.PartyName, PartyMobile = r.PartyMobile,
        RegNo = r.RegNo, VehicleType = r.VehicleType, ChassisNo = r.ChassisNo, Location = r.Location,
        ItemName = r.ItemName, ItemDescription = r.ItemDescription, ItemType = r.ItemType, Qty = r.Qty,
        Rate = ParseDecimal(r.Rate), Total = ParseDecimal(r.Total), SgstPer = ParseDecimal(r.SgstPer),
        SgstAmount = ParseDecimal(r.SgstAmount), CgstPer = ParseDecimal(r.CgstPer),
        CgstAmount = ParseDecimal(r.CgstAmount), IgstPer = ParseDecimal(r.IgstPer),
        IgstAmount = ParseDecimal(r.IgstAmount), Discount = ParseDecimal(r.Discount),
        TotalAmount = ParseDecimal(r.TotalAmount), Mrp = ParseDecimal(r.Mrp), DealerType = r.DealerType,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void UpdateLineOrderReport(DmsLineOrderReport e, DmsLineOrderReport n)
    {
        e.DealerName = n.DealerName; e.DealerCode = n.DealerCode;
        e.DocDate = n.DocDate; e.DocNo = n.DocNo; e.DocType = n.DocType; e.JobDate = n.JobDate;
        e.JobNo = n.JobNo; e.BrandName = n.BrandName; e.Model = n.Model; e.JobCardType = n.JobCardType;
        e.PaymentMode = n.PaymentMode; e.PartyName = n.PartyName; e.PartyMobile = n.PartyMobile;
        e.RegNo = n.RegNo; e.VehicleType = n.VehicleType; e.ChassisNo = n.ChassisNo; e.Location = n.Location;
        e.ItemName = n.ItemName; e.ItemDescription = n.ItemDescription; e.ItemType = n.ItemType;
        e.Qty = n.Qty; e.Rate = n.Rate; e.Total = n.Total; e.SgstPer = n.SgstPer;
        e.SgstAmount = n.SgstAmount; e.CgstPer = n.CgstPer; e.CgstAmount = n.CgstAmount;
        e.IgstPer = n.IgstPer; e.IgstAmount = n.IgstAmount; e.Discount = n.Discount;
        e.TotalAmount = n.TotalAmount; e.Mrp = n.Mrp; e.DealerType = n.DealerType;
        e.UpdatedAt = DateTime.UtcNow;
    }

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        if (DateTime.TryParseExact(val, "dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d))
            return DateOnly.FromDateTime(d);
        if (DateTime.TryParse(val, out var d2))
            return DateOnly.FromDateTime(d2);
        return null;
    }

    private static decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        return decimal.TryParse(val, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}