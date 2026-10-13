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

    private static string BuildUniqueKey(string? dealerCode, string? jobNo, DateOnly? date, string? chassisNo)
        => $"{dealerCode?.Trim().ToUpperInvariant()}{jobNo?.Trim().ToUpperInvariant()}{date?.ToString("yyyy-MM-dd")}{chassisNo?.Trim().ToUpperInvariant()}";

    private static string BuildLorUniqueKey(
        string? dealerCode, string? jobNo, DateOnly? jobDate,
        string? chassisNo, string? itemName, string? itemDescription)
        => $"{dealerCode?.Trim().ToUpperInvariant()}" +
           $"{jobNo?.Trim().ToUpperInvariant()}" +
           $"{jobDate?.ToString("yyyy-MM-dd")}" +
           $"{chassisNo?.Trim().ToUpperInvariant()}" +
           $"{itemName?.Trim().ToUpperInvariant()}" +
           $"{itemDescription?.Trim().ToUpperInvariant()}";

    // ─────────────────────────────────────────────────────────
    // DEALERS (ERP pincode-based) — INSERT ONLY. Existing DealerCode
    // match = skip, not update.
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

            int processed = 0;
            int skippedAsExisting = 0;
            var seenInThisRun = new HashSet<string>();

            foreach (var pin in pincodes)
            {
                try
                {
                    var dealers = await _erpApi.FetchDealersByPinAsync(pin, token);
                    result.RecordsFetched += dealers.Count;

                    foreach (var d in dealers)
                    {
                        if (string.IsNullOrEmpty(d.DealerCode)) continue;

                        var key = d.DealerCode.Trim().ToUpperInvariant();

                        if (seenInThisRun.Contains(key))
                        {
                            skippedAsExisting++;
                            continue;
                        }

                        var existing = await db.DmsDealers
                            .FirstOrDefaultAsync(x => x.DealerCode == d.DealerCode);

                        if (existing == null)
                        {
                            db.DmsDealers.Add(MapDealer(d));
                            result.RecordsInserted++;
                            seenInThisRun.Add(key);
                        }
                        else
                        {
                            skippedAsExisting++;
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
                    log.RecordsUpdated  = 0;
                    await db.SaveChangesAsync();
                }
            }

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist, insert-only mode).";
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
        log.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // DEALERS (BAPL source) — INSERT ONLY.
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
        int skippedAsExisting = 0;

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

            var existingCodes = (await db.DmsDealers
                .Where(d => d.DealerCode != null)
                .Select(d => d.DealerCode!)
                .ToListAsync())
                .Select(c => c.Trim().ToUpperInvariant())
                .ToHashSet();

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

                    var key = dealerCode.Trim().ToUpperInvariant();

                    // FIX: INSERT ONLY — skip if already present.
                    if (existingCodes.Contains(key))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    var dealerCompany   = GetStr("CustomerName") ?? GetStr("Name") ?? GetStr("DealerName");
                    var pinCode         = GetStr("PinCode") ?? GetStr("Pincode") ?? GetStr("Pin");
                    var contactNo       = GetStr("ContactNo") ?? GetStr("MobileNo") ?? GetStr("Mobile");
                    var activeStatusRaw = GetStr("ActiveStatus") ?? GetStr("IsActive") ?? GetStr("Status");
                    var stateName       = GetStr("StateName") ?? GetStr("State");
                    var cityName        = GetStr("CityName") ?? GetStr("City");

                    var activeStatus = activeStatusRaw?.Trim().ToUpperInvariant() == "Y" ? "Active" : "Inactive";

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
                    existingCodes.Add(key);

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

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";
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
        log.RecordsUpdated  = 0;
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

    // ─────────────────────────────────────────────────────────
    // SERVICE HISTORY (single date) — INSERT ONLY
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

            var afterTotalFilter = jobs.Where(j => j.JobNo != "Total").ToList();
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

            var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
            var existingKeySet = (await db.DmsServiceHistories
                .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                .Select(x => x.UniqueKey!)
                .ToListAsync()).ToHashSet();

            int skippedAsExisting = 0;
            int skippedOnError = 0;

            foreach (var parsed in dedupedRecords)
            {
                try
                {
                    // FIX: INSERT ONLY — skip if already exists.
                    if (existingKeySet.Contains(parsed.UniqueKey!))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    db.DmsServiceHistories.Add(parsed);
                    result.RecordsInserted++;
                    existingKeySet.Add(parsed.UniqueKey!);
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping job {no} for {dc}: {msg}",
                        parsed.JobNo, parsed.DealerCode, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // SERVICE HISTORY (range) — INSERT ONLY
    // ─────────────────────────────────────────────────────────

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

            var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
            var existingKeys = await db.DmsServiceHistories
                .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                .Select(x => x.UniqueKey!)
                .ToListAsync();
            var existingKeySet = existingKeys.ToHashSet();

            int skippedAsExisting = 0;
            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var parsed in dedupedRecords)
            {
                try
                {
                    if (existingKeySet.Contains(parsed.UniqueKey!))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    db.DmsServiceHistories.Add(parsed);
                    result.RecordsInserted++;
                    existingKeySet.Add(parsed.UniqueKey!);

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
                $"{droppedAsDuplicateInBatch} in-batch duplicate, {skippedAsExisting} skipped (already exist), " +
                $"{skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
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
        var failedChunks = new List<string>();

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

                // FIX: a failed month used to be dropped silently (the loop never looked at
                // r.Error), leaving a permanent gap in ServiceHistory after the nightly
                // truncate while the run still reported "Success". Retry once (insert-only,
                // so safe) and record anything that still fails — same pattern as VSR/VDR.
                if (!string.IsNullOrEmpty(r.Error))
                {
                    _logger.LogWarning("ServiceHistory window {from}-{to} failed, retrying once: {err}",
                        chunkStart.ToShortDateString(), chunkEnd.ToShortDateString(), r.Error);

                    var retry = await SyncServiceHistoryForRangeAsync(chunkStart, chunkEnd);
                    totalResult.RecordsFetched  += retry.RecordsFetched;
                    totalResult.RecordsInserted += retry.RecordsInserted;

                    if (!string.IsNullOrEmpty(retry.Error))
                        failedChunks.Add($"{chunkStart:yyyy-MM-dd}..{chunkEnd:yyyy-MM-dd}: {retry.Error}");
                }

                chunkStart = chunkEnd.AddDays(1);
            }

            if (failedChunks.Any())
            {
                topLog.Status = "PartialFailure";
                topLog.ErrorMessage = $"{failedChunks.Count} window(s) failed after retry — ServiceHistory data is INCOMPLETE for: "
                    + string.Join(" | ", failedChunks);
                totalResult.Error = topLog.ErrorMessage;
                _logger.LogError("DJR backfill finished with {n} unrecoverable window(s): {details}",
                    failedChunks.Count, topLog.ErrorMessage);
            }
            else
            {
                topLog.Status = "Success";
            }
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
        topLog.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE SALES (single date) — INSERT ONLY
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

            int skippedAsExisting = 0;
            int skippedOnError = 0;

            foreach (var dealerCode in dealerCodes)
            {
                try
                {
                    var sales = await _erpApi.FetchVsrAsync(dealerCode, date, date, token);
                    result.RecordsFetched += sales.Count;

                    var afterBlankFilter = sales
                        .Where(s => !string.IsNullOrEmpty(s.ChassisNo))
                        .ToList();

                    var dedupedRecords = afterBlankFilter
                        .GroupBy(s => s.ChassisNo!.Trim().ToUpperInvariant())
                        .Select(g => g.Last())
                        .ToList();

                    var chassisKeys = dedupedRecords.Select(s => s.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
                    var existingKeySet = (await db.DmsVehicleSales
                        .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                        .Select(x => x.ChassisNo!)
                        .ToListAsync())
                        .Select(c => c.Trim().ToUpperInvariant())
                        .ToHashSet();

                    foreach (var sale in dedupedRecords)
                    {
                        try
                        {
                            var parsed = ParseVehicleSale(sale);
                            var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                            if (existingKeySet.Contains(key))
                            {
                                skippedAsExisting++;
                                continue;
                            }

                            db.DmsVehicleSales.Add(parsed);
                            result.RecordsInserted++;
                            existingKeySet.Add(key);
                        }
                        catch (Exception ex)
                        {
                            skippedOnError++;
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

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE SALES (range) — INSERT ONLY
    // ─────────────────────────────────────────────────────────

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

            var chassisKeys = dedupedRecords.Select(s => s.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingKeys = new HashSet<string>();
            foreach (var chunk in chassisKeys.Chunk(500))
            {
                var chunkSet = chunk.ToHashSet();
                var rows = await db.DmsVehicleSales
                    .Where(x => x.ChassisNo != null && chunkSet.Contains(x.ChassisNo!.Trim().ToUpper()))
                    .Select(x => x.ChassisNo!)
                    .ToListAsync();
                foreach (var r in rows) existingKeys.Add(r.Trim().ToUpperInvariant());
            }

            int skippedAsExisting = 0;
            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var sale in dedupedRecords)
            {
                try
                {
                    var parsed = ParseVehicleSale(sale);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingKeys.Contains(key))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    db.DmsVehicleSales.Add(parsed);
                    result.RecordsInserted++;
                    existingKeys.Add(key);

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
                $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
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
        var failedChunks = new List<string>();

        try
        {
            var chunkStart = start;
            while (chunkStart <= end && !ct.IsCancellationRequested)
            {
                var chunkEnd = chunkStart.AddDays(6);
                if (chunkEnd > end) chunkEnd = end;

                var r = await SyncVehicleSalesForRangeAsync(chunkStart, chunkEnd);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;

                if (!string.IsNullOrEmpty(r.Error))
                {
                    _logger.LogWarning("VehicleSales window {from}-{to} failed, retrying once: {err}",
                        chunkStart.ToShortDateString(), chunkEnd.ToShortDateString(), r.Error);

                    var retry = await SyncVehicleSalesForRangeAsync(chunkStart, chunkEnd);
                    totalResult.RecordsFetched  += retry.RecordsFetched;
                    totalResult.RecordsInserted += retry.RecordsInserted;

                    if (!string.IsNullOrEmpty(retry.Error))
                        failedChunks.Add($"{chunkStart:yyyy-MM-dd}..{chunkEnd:yyyy-MM-dd}: {retry.Error}");
                }

                chunkStart = chunkEnd.AddDays(1);
            }

            if (failedChunks.Any())
            {
                topLog.Status = "PartialFailure";
                topLog.ErrorMessage = $"{failedChunks.Count} window(s) failed after retry — VehicleSales data is INCOMPLETE for: "
                    + string.Join(" | ", failedChunks);
                totalResult.Error = topLog.ErrorMessage;
                _logger.LogError("VSR backfill finished with {n} unrecoverable window(s): {details}",
                    failedChunks.Count, topLog.ErrorMessage);
            }
            else
            {
                topLog.Status = "Success";
            }
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
        topLog.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // CALL CENTRE DEALERS — INSERT ONLY
    // ─────────────────────────────────────────────────────────

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

            int skippedAsExisting = 0;
            var seenInThisRun = new HashSet<string>();

            foreach (var pin in pincodes)
            {
                try
                {
                    var dealers = await _erpApi.FetchCallCentreDealersByPinAsync(pin, token);
                    result.RecordsFetched += dealers.Count;

                    foreach (var d in dealers)
                    {
                        if (string.IsNullOrEmpty(d.DealerCode)) continue;
                        var key = d.DealerCode.Trim().ToUpperInvariant();

                        if (seenInThisRun.Contains(key)) { skippedAsExisting++; continue; }

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
                            seenInThisRun.Add(key);
                        }
                        else
                        {
                            skippedAsExisting++;
                        }
                    }
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("CallCentre pin {pin} failed: {msg}", pin, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist). INSERT-ONLY mode.";
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
        log.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE DISPATCHES (range) — INSERT ONLY
    // ─────────────────────────────────────────────────────────

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

            var chassisKeys = dedupedRecords.Select(d => d.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingKeys = new HashSet<string>();
            foreach (var chunk in chassisKeys.Chunk(500))
            {
                var chunkSet = chunk.ToHashSet();
                var rows = await db.DmsVehicleDispatches
                    .Where(x => x.ChassisNo != null && chunkSet.Contains(x.ChassisNo!.Trim().ToUpper()))
                    .Select(x => x.ChassisNo!)
                    .ToListAsync();
                foreach (var r in rows) existingKeys.Add(r.Trim().ToUpperInvariant());
            }

            int skippedAsExisting = 0;
            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var d in dedupedRecords)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingKeys.Contains(key))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    db.DmsVehicleDispatches.Add(parsed);
                    result.RecordsInserted++;
                    existingKeys.Add(key);

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
                $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE DISPATCHES (single date) — INSERT ONLY
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

            var afterBlankFilter = dispatches
                .Where(d => !string.IsNullOrEmpty(d.ChassisNo))
                .ToList();

            var dedupedRecords = afterBlankFilter
                .GroupBy(d => d.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();

            var chassisKeys = dedupedRecords.Select(d => d.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingKeySet = (await db.DmsVehicleDispatches
                .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                .Select(x => x.ChassisNo!)
                .ToListAsync())
                .Select(c => c.Trim().ToUpperInvariant())
                .ToHashSet();

            int skippedAsExisting = 0;
            int skippedOnError = 0;

            foreach (var d in dedupedRecords)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingKeySet.Contains(key))
                    {
                        skippedAsExisting++;
                        continue;
                    }

                    db.DmsVehicleDispatches.Add(parsed);
                    result.RecordsInserted++;
                    existingKeySet.Add(key);
                }
                catch (Exception ex)
                {
                    skippedOnError++;
                    _logger.LogWarning("Skipping dispatch (chassis {ch}): {msg}",
                        d.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedAsExisting} skipped (already exist), {skippedOnError} per-record error(s). INSERT-ONLY mode.";

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
        log.RecordsUpdated  = 0;
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
        var failedChunks = new List<string>();

        try
        {
            var chunkStart = start;
            while (chunkStart <= end && !ct.IsCancellationRequested)
            {
                var chunkEnd = chunkStart.AddDays(6);
                if (chunkEnd > end) chunkEnd = end;

                var r = await SyncVehicleDispatchesForRangeAsync(chunkStart, chunkEnd);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;

                if (!string.IsNullOrEmpty(r.Error))
                {
                    _logger.LogWarning("VehicleDispatches window {from}-{to} failed, retrying once: {err}",
                        chunkStart.ToShortDateString(), chunkEnd.ToShortDateString(), r.Error);

                    var retry = await SyncVehicleDispatchesForRangeAsync(chunkStart, chunkEnd);
                    totalResult.RecordsFetched  += retry.RecordsFetched;
                    totalResult.RecordsInserted += retry.RecordsInserted;

                    if (!string.IsNullOrEmpty(retry.Error))
                        failedChunks.Add($"{chunkStart:yyyy-MM-dd}..{chunkEnd:yyyy-MM-dd}: {retry.Error}");
                }

                chunkStart = chunkEnd.AddDays(1);
            }

            if (failedChunks.Any())
            {
                topLog.Status = "PartialFailure";
                topLog.ErrorMessage = $"{failedChunks.Count} window(s) failed after retry — VehicleDispatches data is INCOMPLETE for: "
                    + string.Join(" | ", failedChunks);
                totalResult.Error = topLog.ErrorMessage;
                _logger.LogError("VDR backfill finished with {n} unrecoverable window(s): {details}",
                    failedChunks.Count, topLog.ErrorMessage);
            }
            else
            {
                topLog.Status = "Success";
            }
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
        topLog.RecordsUpdated  = 0;
        await db.SaveChangesAsync();

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // LINE ORDER REPORT — INSERT ONLY (unchanged from your version)
    // ─────────────────────────────────────────────────────────

    // failedDealers (optional, added last so existing callers are unaffected): every dealer whose
    // fetch/insert threw is added here, so a caller can retry just those instead of losing them silently.
    public async Task<SyncResult> SyncLineOrderReportAsync(
        DateTime startDate, DateTime endDate,
        CancellationToken ct = default,
        List<string>? dealerCodesOverride = null,
        List<string>? failedDealers = null)
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
                    result.Error     = log.ErrorMessage;
                    log.CompletedAt  = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return result;
                }
            }

            int droppedAsEmptyTotal = 0;
            int droppedAsDuplicateTotal = 0;
            int skippedAsExistingTotal = 0;
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

                    var afterEmptyFilter = parsedRecords
                        .Where(p =>
                            !string.IsNullOrEmpty(p.DealerCode) || !string.IsNullOrEmpty(p.JobNo) ||
                            p.JobDate != null || !string.IsNullOrEmpty(p.ChassisNo) ||
                            !string.IsNullOrEmpty(p.ItemName) || !string.IsNullOrEmpty(p.ItemDescription))
                        .ToList();
                    droppedAsEmptyTotal += parsedRecords.Count - afterEmptyFilter.Count;

                    var dedupedRecords = afterEmptyFilter
                        .GroupBy(p => p.UniqueKey)
                        .Select(g => g.Last())
                        .ToList();
                    droppedAsDuplicateTotal += afterEmptyFilter.Count - dedupedRecords.Count;

                    if (!dedupedRecords.Any()) continue;

                    var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
                    var existingKeys = (await db.DmsLineOrderReports
                        .Where(x => x.DealerCode == dealerCode && x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                        .Select(x => x.UniqueKey!)
                        .ToListAsync()).ToHashSet();

                    foreach (var parsed in dedupedRecords)
                    {
                        try
                        {
                            if (existingKeys.Contains(parsed.UniqueKey!))
                            {
                                skippedAsExistingTotal++;
                                continue;
                            }

                            db.DmsLineOrderReports.Add(parsed);
                            result.RecordsInserted++;
                            existingKeys.Add(parsed.UniqueKey!);

                            saveCounter++;
                            if (saveCounter % SaveBatchSize == 0)
                                await db.SaveChangesAsync();
                        }
                        catch (Exception ex)
                        {
                            if (ex.InnerException?.Message.Contains("UX_LineOrderReport_UniqueKey") == true)
                            {
                                _logger.LogInformation(
                                    "Concurrent insert detected for job {jn}, item {it} — already added, skipping.",
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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("LOR fetch failed for dealer {dc}: {msg}", dealerCode, ex.Message);
                    failedDealers?.Add(dealerCode);
                }
            }

            log.ErrorMessage =
                $"Dropped: {droppedAsEmptyTotal} completely-empty-record, {droppedAsDuplicateTotal} in-batch duplicate, " +
                $"{skippedAsExistingTotal} skipped (already exist), {skippedOnErrorTotal} per-record error(s). " +
                $"INSERT-ONLY mode.";
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
        log.RecordsUpdated  = 0;
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

    // NOTE: SyncLineOrderReportAsync puts its "Dropped: …" statistics into result.Error even on
    // SUCCESS. Only treat Error as a failure when it is not that statistics string.
    private static bool IsLorFailure(SyncResult r)
        => !string.IsNullOrEmpty(r.Error) && !r.Error.StartsWith("Dropped:", StringComparison.Ordinal);

    // LOR is fetched as ONE call per dealer for the whole range, which can time out for large
    // dealers. Pass 1 does that and records the dealers that failed (they used to be dropped
    // silently). Pass 2 re-fetches only those dealers in yearly slices. Anything still failing is
    // returned in .Error so the nightly reload is marked PartialFailure, not "Success".
    // .Error is only set on real failure here (never the statistics string).
    public async Task<SyncResult> BackfillLineOrderReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        var failedDealers = new List<string>();
        var pass1 = await SyncLineOrderReportAsync(start, end, ct, failedDealers: failedDealers);

        var total = new SyncResult
        {
            SyncType        = "BackfillLineOrderReport",
            Date            = end,
            RecordsFetched  = pass1.RecordsFetched,
            RecordsInserted = pass1.RecordsInserted
        };

        if (IsLorFailure(pass1))
        {
            total.Error = pass1.Error;
            return total;
        }

        var stillFailed = new List<string>();

        foreach (var dealer in failedDealers.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning("LOR: retrying dealer {dc} in yearly slices after a failed full-range fetch.", dealer);

            var dealerFailed = false;
            var sliceStart = start;
            while (sliceStart <= end)
            {
                var sliceEnd = sliceStart.AddYears(1).AddDays(-1);
                if (sliceEnd > end) sliceEnd = end;

                var sliceFailed = new List<string>();
                var r = await SyncLineOrderReportAsync(
                    sliceStart, sliceEnd, ct, new List<string> { dealer }, sliceFailed);

                total.RecordsFetched  += r.RecordsFetched;
                total.RecordsInserted += r.RecordsInserted;

                if (sliceFailed.Count > 0 || IsLorFailure(r))
                    dealerFailed = true;

                sliceStart = sliceEnd.AddDays(1);
            }

            if (dealerFailed) stillFailed.Add(dealer);
        }

        if (stillFailed.Count > 0)
        {
            total.Error = $"LineOrderReport INCOMPLETE for {stillFailed.Count} dealer(s) after retry: "
                + string.Join(", ", stillFailed.Take(30))
                + (stillFailed.Count > 30 ? ", …" : "");
            _logger.LogError("LOR backfill finished with {n} dealer(s) still failing: {dealers}",
                stillFailed.Count, string.Join(", ", stillFailed));
        }

        return total;
    }

    // ─────────────────────────────────────────────────────────
    // TRUNCATE ALL DATA TABLES
    // ─────────────────────────────────────────────────────────
    private static readonly HashSet<string> AllowedTruncateTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "DMS_ServiceHistory",
        "DMS_VehicleSales",
        "DMS_VehicleDispatches",
        "DMS_LineOrderReport"
    };

    public async Task TruncateAllDataTablesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tables = new[]
        {
            "DMS_ServiceHistory",
            "DMS_VehicleSales",
            "DMS_VehicleDispatches",
            "DMS_LineOrderReport",
            //"DMS_Dealers",
            //"DMS_CallCentreDealers"
        };

        foreach (var table in tables)
        {
            if (!AllowedTruncateTables.Contains(table))
                throw new InvalidOperationException($"Refusing to truncate unrecognized table '{table}'.");
            try
            {
        #pragma warning disable EF1002 // table name validated against AllowedTruncateTables immediately above
                await db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE [{table}]");
        #pragma warning restore EF1002
                _logger.LogInformation("Truncated {table}", table);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to truncate {table} — check for foreign key references", table);
                throw;
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // NIGHTLY FULL RELOAD — truncate everything, then re-fetch
    // HistoricalStartDate→today fresh. Started at Sync:DailyTimeIst by SyncHostedService (and on
    // demand by NightlyReloadController) through NightlyReloadRunner, which guarantees only one
    // reload runs at a time.
    //
    //  1. PREFLIGHT  — ERP login + one real DJR call must succeed BEFORE anything is truncated.
    //                  If the ERP is down at midnight the existing data is left untouched (it
    //                  re-checks every 10 min, up to 6 times) instead of being wiped and
    //                  replaced with nothing.
    //  2. TRUNCATE   — ServiceHistory, VehicleSales, VehicleDispatches, LineOrderReport.
    //  3. RE-FETCH   — dealers, then each report; failed chunks/dealers are retried and
    //                  reported, never swallowed.
    //  4. VALIDATE   — every table must have rows. "Success" only when there were no failed
    //                  chunks AND no empty tables; otherwise "PartialFailure".
    //  Outcome is written to the sync log (DmsSyncLogs, SyncType = 'NightlyFullReload').
    // ─────────────────────────────────────────────────────────
    public async Task<SyncResult> RunNightlyFullReloadAsync(CancellationToken ct = default)
    {
        var overall   = new SyncResult { SyncType = "NightlyFullReload" };
        var problems  = new List<string>();
        var truncated = false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "NightlyFullReload", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01";
        var start = DateTime.Parse(startDateStr);
        var end   = DateTime.UtcNow.AddMinutes(330).Date;   // "till today" in IST (UTC+5:30)

        async Task Step(string text)
        {
            _logger.LogInformation("Nightly full reload — {step}", text);
            log.ErrorMessage = Trunc("Progress: " + text, 1800);
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning("Could not save reload progress: {msg}", ex.Message); }
        }

        void Collect(string name, SyncResult r)
        {
            overall.RecordsFetched  += r.RecordsFetched;
            overall.RecordsInserted += r.RecordsInserted;
            if (!string.IsNullOrEmpty(r.Error)) problems.Add($"{name}: {r.Error}");
        }

        try
        {
            // 1 ── PREFLIGHT (nothing is changed until this passes)
            await Step("preflight: checking ERP login and report API");
            await WaitForErpHealthyAsync(end, ct);

            // 2 ── TRUNCATE
            await Step("truncating ServiceHistory, VehicleSales, VehicleDispatches, LineOrderReport");
            truncated = true;   // set BEFORE the call: a failure part-way leaves the tables in an unknown state
            await TruncateAllDataTablesAsync();

            // 3 ── RE-FETCH
            await Step("dealers (pincode sync)");
            Collect("Dealers", await SyncAllDealersAsync());
            ct.ThrowIfCancellationRequested();

            await Step("call-centre dealers");
            Collect("CallCentreDealers", await SyncCallCentreDealersAsync());
            ct.ThrowIfCancellationRequested();

            await Step($"ServiceHistory {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
            Collect("ServiceHistory", await BackfillHistoricalDataAsync(start, end, ct));
            ct.ThrowIfCancellationRequested();

            await Step($"VehicleSales {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
            Collect("VehicleSales", await BackfillVehicleSalesAsync(start, end, ct));
            ct.ThrowIfCancellationRequested();

            await Step($"VehicleDispatches {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
            Collect("VehicleDispatches", await BackfillVehicleDispatchesAsync(start, end, ct));
            ct.ThrowIfCancellationRequested();

            await Step($"LineOrderReport {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
            Collect("LineOrderReport", await BackfillLineOrderReportAsync(start, end, ct));
            ct.ThrowIfCancellationRequested();

            // 4 ── VALIDATE
            await Step("validating row counts");
            var nService  = await db.DmsServiceHistories.CountAsync(CancellationToken.None);
            var nSales    = await db.DmsVehicleSales.CountAsync(CancellationToken.None);
            var nDispatch = await db.DmsVehicleDispatches.CountAsync(CancellationToken.None);
            var nLor      = await db.DmsLineOrderReports.CountAsync(CancellationToken.None);

            if (nService  == 0) problems.Add("ServiceHistory table is EMPTY after reload");
            if (nSales    == 0) problems.Add("VehicleSales table is EMPTY after reload");
            if (nDispatch == 0) problems.Add("VehicleDispatches table is EMPTY after reload");
            if (nLor      == 0) problems.Add("LineOrderReport table is EMPTY after reload");

            var counts = $"rows: ServiceHistory={nService}, VehicleSales={nSales}, VehicleDispatches={nDispatch}, LineOrderReport={nLor}";

            if (problems.Count == 0)
            {
                log.Status       = "Success";
                log.ErrorMessage = Trunc("OK — " + counts, 1800);
                _logger.LogInformation("Nightly full reload complete: {fet} fetched, {ins} inserted. {counts}",
                    overall.RecordsFetched, overall.RecordsInserted, counts);
            }
            else
            {
                log.Status       = "PartialFailure";
                log.ErrorMessage = Trunc(string.Join(" | ", problems) + " || " + counts, 1800);
                overall.Error    = log.ErrorMessage;
                _logger.LogError("Nightly full reload finished WITH PROBLEMS: {problems} || {counts}",
                    string.Join(" | ", problems), counts);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status = "Interrupted";
            log.ErrorMessage = truncated
                ? "INTERRUPTED (app shutting down) AFTER truncate — tables are only partly loaded. Run the reload again."
                : "Interrupted before truncate — no data was changed.";
            overall.Error = log.ErrorMessage;
            _logger.LogWarning("Nightly full reload interrupted: {msg}", log.ErrorMessage);
            throw;
        }
        catch (Exception ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            log.Status = "Failed";
            log.ErrorMessage = Trunc(
                (truncated
                    ? "FAILED AFTER truncate — tables may be empty or partial. "
                    : "FAILED BEFORE truncate — existing data untouched. ") + reason, 1800);
            overall.Error = log.ErrorMessage;
            _logger.LogError(ex, "Nightly full reload failed ({where} truncate)", truncated ? "after" : "before");
        }
        finally
        {
            log.CompletedAt     = DateTime.UtcNow;
            log.RecordsFetched  = overall.RecordsFetched;
            log.RecordsInserted = overall.RecordsInserted;
            log.RecordsUpdated  = 0;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Could not write the NightlyFullReload sync-log row"); }
        }

        return overall;
    }

    // Do not truncate until the ERP can actually serve data: login must work and one real DJR
    // call (the latest day) must come back without error. Re-checks every 10 minutes, 6 times.
    private async Task WaitForErpHealthyAsync(DateTime probeDate, CancellationToken ct)
    {
        const int maxAttempts = 6;
        var waitBetween = TimeSpan.FromMinutes(10);
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var token = await _erpApi.GetValidTokenAsync();
                await _erpApi.FetchDjrAsync(probeDate, token, "");   // throws if the call fails after its own retries
                _logger.LogInformation("ERP preflight OK (attempt {a}/{max}).", attempt, maxAttempts);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _logger.LogWarning("ERP preflight failed (attempt {a}/{max}): {msg}", attempt, maxAttempts, ex.Message);
                if (attempt < maxAttempts)
                    await Task.Delay(waitBetween, ct);
            }
        }

        throw new InvalidOperationException(
            $"ERP not healthy after {maxAttempts} preflight attempts — NOTHING was truncated. Last error: {last?.Message}", last);
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    // ─────────────────────────────────────────────────────────
    // UPSERT VARIANTS — used only by the PUT endpoints in
    // DataController. Insert new records AND update existing matches,
    // unlike the POST/insert-only Sync* methods above.
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> UpsertServiceHistoryForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType  = "ServiceHistoryUpsert",
            SyncDate  = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow,
            Status    = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "ServiceHistoryUpsert", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var jobs = await _erpApi.FetchDjrRangeAsync(startDate, endDate, token);
            result.RecordsFetched = jobs.Count;

            var afterFilter = jobs
                .Where(j => j.JobNo != "Total"
                        && !string.IsNullOrEmpty(j.DealerCode)
                        && !string.IsNullOrEmpty(j.JobNo))
                .ToList();

            var parsedRecords = afterFilter
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

            var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
            var existingRaw = await db.DmsServiceHistories
                .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                .ToListAsync();
            var existingLookup = existingRaw
                .Where(x => x.UniqueKey != null)
                .GroupBy(x => x.UniqueKey!)
                .ToDictionary(g => g.Key, g => g.First());

            int saveCounter = 0;
            int skippedOnError = 0;

            foreach (var parsed in dedupedRecords)
            {
                try
                {
                    if (existingLookup.TryGetValue(parsed.UniqueKey!, out var existing))
                    {
                        existing.JobDate = parsed.JobDate; existing.DocNo = parsed.DocNo;
                        existing.DocType = parsed.DocType; existing.DocDate = parsed.DocDate;
                        existing.InvoiceDate = parsed.InvoiceDate; existing.NetTotal = parsed.NetTotal;
                        existing.EstimatedJobExpenses = parsed.EstimatedJobExpenses;
                        existing.Parts = parsed.Parts; existing.Labour = parsed.Labour;
                        existing.Gstamount = parsed.Gstamount; existing.Igstamount = parsed.Igstamount;
                        existing.TotalWotax = parsed.TotalWotax; existing.UpdatedAt = DateTime.UtcNow;
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
                    _logger.LogWarning("Upsert skip job {no} for {dc}: {msg}", parsed.JobNo, parsed.DealerCode, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedOnError} per-record error(s). UPSERT mode.";
            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "ServiceHistory upsert failed");
        }

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
        await db.SaveChangesAsync();
        return result;
    }

    public async Task<SyncResult> UpsertVehicleSalesForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType = "VehicleSalesUpsert", SyncDate = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow, Status = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleSalesUpsert", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var sales = await _erpApi.FetchVsrAsync("", startDate, endDate, token);
            result.RecordsFetched = sales.Count;

            var dedupedRecords = sales
                .Where(s => !string.IsNullOrEmpty(s.ChassisNo))
                .GroupBy(s => s.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();

            var chassisKeys = dedupedRecords.Select(s => s.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingRaw = await db.DmsVehicleSales
                .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                .ToListAsync();
            var existingLookup = existingRaw
                .Where(x => x.ChassisNo != null)
                .GroupBy(x => x.ChassisNo!.Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            int saveCounter = 0;
            int skippedOnError = 0;

            foreach (var sale in dedupedRecords)
            {
                try
                {
                    var parsed = ParseVehicleSale(sale);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingLookup.TryGetValue(key, out var existing))
                    {
                        existing.InvoiceNo = parsed.InvoiceNo; existing.InvoiceDate = parsed.InvoiceDate;
                        existing.NetAmount = parsed.NetAmount; existing.ItemRate = parsed.ItemRate;
                        existing.FinancedBy = parsed.FinancedBy; existing.FinAmount = parsed.FinAmount;
                        existing.SoldTo = parsed.SoldTo; existing.CusMob = parsed.CusMob;
                        existing.UpdatedAt = DateTime.UtcNow;
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
                    _logger.LogWarning("Upsert skip sale (chassis {ch}): {msg}", sale.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedOnError} per-record error(s). UPSERT mode.";
            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "VehicleSales upsert failed");
        }

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
        await db.SaveChangesAsync();
        return result;
    }

    public async Task<SyncResult> UpsertVehicleDispatchesForRangeAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType = "VehicleDispatchesUpsert", SyncDate = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow, Status = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "VehicleDispatchesUpsert", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            var dispatches = await _erpApi.FetchVdrAsync(startDate, endDate, token);
            result.RecordsFetched = dispatches.Count;

            var dedupedRecords = dispatches
                .Where(d => !string.IsNullOrEmpty(d.ChassisNo))
                .GroupBy(d => d.ChassisNo!.Trim().ToUpperInvariant())
                .Select(g => g.Last())
                .ToList();

            var chassisKeys = dedupedRecords.Select(d => d.ChassisNo!.Trim().ToUpperInvariant()).ToHashSet();
            var existingRaw = await db.DmsVehicleDispatches
                .Where(x => x.ChassisNo != null && chassisKeys.Contains(x.ChassisNo!.Trim().ToUpper()))
                .ToListAsync();
            var existingLookup = existingRaw
                .Where(x => x.ChassisNo != null)
                .GroupBy(x => x.ChassisNo!.Trim().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            int saveCounter = 0;
            int skippedOnError = 0;

            foreach (var d in dedupedRecords)
            {
                try
                {
                    var parsed = MapVehicleDispatch(d);
                    var key = parsed.ChassisNo!.Trim().ToUpperInvariant();

                    if (existingLookup.TryGetValue(key, out var existing))
                    {
                        existing.VehicleStatus = parsed.VehicleStatus; existing.InvoiceNo = parsed.InvoiceNo;
                        existing.InvoiceDate = parsed.InvoiceDate; existing.NameOfParty = parsed.NameOfParty;
                        existing.MobileNo = parsed.MobileNo; existing.FinancerName = parsed.FinancerName;
                        existing.FinAmount = parsed.FinAmount; existing.UpdatedAt = DateTime.UtcNow;
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
                    _logger.LogWarning("Upsert skip dispatch (chassis {ch}): {msg}", d.ChassisNo, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedOnError} per-record error(s). UPSERT mode.";
            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "VehicleDispatches upsert failed");
        }

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
        await db.SaveChangesAsync();
        return result;
    }

    public async Task<SyncResult> UpsertLineOrderReportAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog
        {
            SyncType = "LineOrderReportUpsert", SyncDate = DateOnly.FromDateTime(endDate),
            StartedAt = DateTime.UtcNow, Status = "Running"
        };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = "LineOrderReportUpsert", Date = endDate };

        try
        {
            var token = await _erpApi.GetValidTokenAsync();

            var dealerCodes = await db.DmsDealers
                .Where(d => d.DealerCode != null)
                .Select(d => d.DealerCode!)
                .Distinct()
                .ToListAsync();

            if (!dealerCodes.Any())
            {
                log.Status = "Failed";
                log.ErrorMessage = "No dealers in DMS_Dealers. Run dealer sync first.";
                log.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return result;
            }

            int skippedOnError = 0;
            int saveCounter = 0;

            foreach (var dealerCode in dealerCodes)
            {
                await Task.Delay(200);
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

                    var dedupedRecords = parsedRecords
                        .GroupBy(p => p.UniqueKey)
                        .Select(g => g.Last())
                        .ToList();

                    if (!dedupedRecords.Any()) continue;

                    var candidateKeys = dedupedRecords.Select(p => p.UniqueKey).ToHashSet();
                    var existingRaw = await db.DmsLineOrderReports
                        .Where(x => x.DealerCode == dealerCode && x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
                        .ToListAsync();
                    var existingLookup = existingRaw
                        .Where(x => x.UniqueKey != null)
                        .GroupBy(x => x.UniqueKey!)
                        .ToDictionary(g => g.Key, g => g.First());

                    foreach (var parsed in dedupedRecords)
                    {
                        try
                        {
                            if (existingLookup.TryGetValue(parsed.UniqueKey!, out var existing))
                            {
                                existing.Rate = parsed.Rate; existing.Total = parsed.Total;
                                existing.TotalAmount = parsed.TotalAmount; existing.Discount = parsed.Discount;
                                existing.SgstAmount = parsed.SgstAmount; existing.CgstAmount = parsed.CgstAmount;
                                existing.IgstAmount = parsed.IgstAmount; existing.DocNo = parsed.DocNo;
                                existing.DocDate = parsed.DocDate; existing.UpdatedAt = DateTime.UtcNow;
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
                            skippedOnError++;
                            _logger.LogWarning("Upsert skip LOR (dealer {dc}, job {jn}): {msg}",
                                dealerCode, parsed.JobNo, ex.Message);
                        }
                    }

                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("LOR upsert fetch failed for dealer {dc}: {msg}", dealerCode, ex.Message);
                }
            }

            log.ErrorMessage = $"{skippedOnError} per-record error(s). UPSERT mode.";
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.InnerException?.Message ?? ex.Message;
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "LOR upsert failed");
        }

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
        await db.SaveChangesAsync();
        return result;
    }

    // ─────────────────────────────────────────────────────────
    // MAPPING HELPERS — unchanged (Update* helpers deleted, no
    // longer called anywhere since everything is insert-only now)
    // ─────────────────────────────────────────────────────────

    private static DmsDealer MapDealer(DealerValue d) => new()
    {
        DealerCode = d.DealerCode, DealerCompany = d.DealerCompany, ContactNo = d.ContactNo,
        AlternateContactNo = d.AlternateContactNo, DealerStateName = d.DealerStateName,
        DealerCityName = d.DealerCityName, PinCode = d.PinCode, ActiveStatus = d.ActiveStatus,
        LastFetchedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

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