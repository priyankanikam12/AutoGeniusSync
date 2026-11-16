using System.Globalization;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Services;

public partial class DataSyncService
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

    // Dealer-wise (one ERP call per dealer per month) + UPSERT. The former single all-dealer ERP call made the
    // ERP throw OutOfMemoryException and the Valid=false answer was stored as "0 rows" — data silently missing.
    public async Task<SyncResult> SyncServiceHistoryForDateAsync(DateTime date)
        => await UpsertRangeViaEngineAsync(SyncApi.ServiceHistory, "ServiceHistory", date.Date, date.Date, await DealersForManualRangeAsync(), null);

    // ─────────────────────────────────────────────────────────
    // SERVICE HISTORY (range) — INSERT ONLY
    // ─────────────────────────────────────────────────────────

    // Dealer-wise (one ERP call per dealer per month) + UPSERT. The former single all-dealer ERP call made the
    // ERP throw OutOfMemoryException and the Valid=false answer was stored as "0 rows" — data silently missing.
    public async Task<SyncResult> SyncServiceHistoryForRangeAsync(DateTime startDate, DateTime endDate)
        => await UpsertRangeViaEngineAsync(SyncApi.ServiceHistory, "ServiceHistory", startDate.Date, endDate.Date, await DealersForManualRangeAsync(), null);

    // DJR window with fallback. The ERP's DJR call is slow and getting slower (13–36 s per month in
    // the production log, and some months time out 3 x 180 s). Order of attempts for a window:
    //   1. whole window   2. same window once more   3. split into halves and repeat, down to
    //      Sync:DjrMinWindowDays (default 1 day). Only a window that still fails at the minimum
    //      size (default 1 day) is recorded in failedChunks (=> PartialFailure with exact dates).
    // Insert-only SyncServiceHistoryForRangeAsync is used, so repeating a window cannot duplicate rows.
    // The ERP sometimes returns malformed JSON (e.g. an unescaped quote inside 'Coupon No'). Retrying the
    // same window returns the same bytes, so a parse error is not retried — the window is split
    // straight away to pin down the exact day instead.
    private static bool IsDeterministicParseError(string? err)
        => err != null && (err.Contains("After parsing a value", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("unexpected character", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("Unterminated string", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("Path '", StringComparison.Ordinal));

    // Historical ServiceHistory (DJR) load. Now month-wise + dealer-wise through the checkpoint engine
    // (DataSyncService.Checkpoint.cs): resumes where it stopped, upserts, never duplicates.
    // No from/to = HistoricalStartDate → today, completed months are skipped.
    // Explicit from/to = that range is fetched again even if completed (repair / re-check).
    public Task<SyncResult> BackfillHistoricalDataAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
        => BackfillViaCheckpointsAsync(SyncApi.ServiceHistory, "BackfillHistorical", fromDate, toDate, forceResync, ct);

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

    // Dealer-wise (one ERP call per dealer per month) + UPSERT. The former single all-dealer ERP call made the
    // ERP throw OutOfMemoryException and the Valid=false answer was stored as "0 rows" — data silently missing.
    public async Task<SyncResult> SyncVehicleSalesForRangeAsync(DateTime startDate, DateTime endDate)
        => await UpsertRangeViaEngineAsync(SyncApi.VehicleSales, "VehicleSales", startDate.Date, endDate.Date, await DealersForManualRangeAsync(), null);

    public Task<SyncResult> BackfillVehicleSalesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
        => BackfillViaCheckpointsAsync(SyncApi.VehicleSales, "BackfillVehicleSales", fromDate, toDate, forceResync, ct);

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

    // Dealer-wise (one ERP call per dealer per month) + UPSERT. The former single all-dealer ERP call made the
    // ERP throw OutOfMemoryException and the Valid=false answer was stored as "0 rows" — data silently missing.
    public async Task<SyncResult> SyncVehicleDispatchesForRangeAsync(DateTime startDate, DateTime endDate)
        => await UpsertRangeViaEngineAsync(SyncApi.VehicleDispatches, "VehicleDispatches", startDate.Date, endDate.Date, await DealersForManualRangeAsync(), null);

    // ─────────────────────────────────────────────────────────
    // VEHICLE DISPATCHES (single date) — INSERT ONLY
    // ─────────────────────────────────────────────────────────

    // Dealer-wise (one ERP call per dealer per month) + UPSERT. The former single all-dealer ERP call made the
    // ERP throw OutOfMemoryException and the Valid=false answer was stored as "0 rows" — data silently missing.
    public async Task<SyncResult> SyncVehicleDispatchesForDateAsync(DateTime date)
        => await UpsertRangeViaEngineAsync(SyncApi.VehicleDispatches, "VehicleDispatches", date.Date, date.Date, await DealersForManualRangeAsync(), null);

    public Task<SyncResult> BackfillVehicleDispatchesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default,
        bool forceResync = false)
        => BackfillViaCheckpointsAsync(SyncApi.VehicleDispatches, "BackfillVehicleDispatches", fromDate, toDate, forceResync, ct);

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
        // Never ask the ERP for more than one month in a single call (memory / ERP load) — split by month.
        if ((endDate.Date - startDate.Date).TotalDays > 31)
            return await SplitByMonthAsync(startDate, endDate, (a, b) => SyncLineOrderReportAsync(a, b, ct, dealerCodesOverride, failedDealers));

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
                    if (IsErpAuthError(ex.Message)) throw;   // access refused: stop, do not try every dealer
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

    private static async Task<SyncResult> SplitByMonthAsync(DateTime startDate, DateTime endDate, Func<DateTime, DateTime, Task<SyncResult>> run)
    {
        var total  = new SyncResult { Date = endDate };
        var errors = new List<string>();
        for (var ms = new DateTime(startDate.Year, startDate.Month, 1); ms <= endDate.Date; ms = ms.AddMonths(1))
        {
            var a = ms < startDate.Date ? startDate.Date : ms;
            var b = ms.AddMonths(1).AddDays(-1);
            if (b > endDate.Date) b = endDate.Date;
            var r = await run(a, b);
            total.SyncType = r.SyncType;
            total.RecordsFetched  += r.RecordsFetched;
            total.RecordsInserted += r.RecordsInserted;
            total.RecordsUpdated  += r.RecordsUpdated;
            if (!string.IsNullOrEmpty(r.Error) && !r.Error.StartsWith("Dropped:", StringComparison.Ordinal))
                errors.Add($"{a:yyyy-MM-dd}..{b:yyyy-MM-dd}: {r.Error}");
            if (IsErpAuthError(r.Error)) break;
        }
        if (errors.Count > 0) total.Error = Trunc(string.Join(" | ", errors), 1800);
        return total;
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
    // ERP answered "Unauthorized" (see ErpAuthorizationException): an access problem, not "no data".
    // Loops stop at the first such error instead of calling the ERP hundreds of times.
    private static bool IsErpAuthError(string? error)
        => error != null && error.Contains("ERP authorization refused", StringComparison.OrdinalIgnoreCase);

    private static bool IsLorFailure(SyncResult r)
        => !string.IsNullOrEmpty(r.Error) && !r.Error.StartsWith("Dropped:", StringComparison.Ordinal);

    // LOR is fetched as ONE call per dealer for the whole range, which can time out for large
    // dealers. Pass 1 does that and records the dealers that failed (they used to be dropped
    // silently). Pass 2 re-fetches only those dealers in yearly slices. Anything still failing is
    // returned in .Error so the nightly reload is marked PartialFailure, not "Success".
    // .Error is only set on real failure here (never the statistics string).
    public Task<SyncResult> BackfillLineOrderReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
        => BackfillViaCheckpointsAsync(SyncApi.LineOrderReport, "BackfillLineOrderReport", fromDate, toDate, false, ct);

    // Shared by the four Backfill* methods above. Writes one DmsSyncLog row (same SyncType names as before).
    private async Task<SyncResult> BackfillViaCheckpointsAsync(
        string api, string logType, DateTime? fromDate, DateTime? toDate, bool forceResync, CancellationToken ct)
    {
        var start = (fromDate ?? HistoricalStartDate()).Date;
        var end   = (toDate ?? TodayIst()).Date;
        var refresh = forceResync || fromDate.HasValue || toDate.HasValue ? DateTime.UtcNow : (DateTime?)null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = new DmsSyncLog { SyncType = logType, SyncDate = DateOnly.FromDateTime(end), StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync(CancellationToken.None);

        SyncResult r;
        try
        {
            r = await RunCheckpointSyncAsync(new CheckpointRunOptions(
                    new[] { api }, start, end, HistoricalPerDealer(), refresh, null, api), ct);
            log.Status = string.IsNullOrEmpty(r.Error) ? "Success" : "PartialFailure";
            log.ErrorMessage = r.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status = "Interrupted";
            log.ErrorMessage = "Stopped (app shutting down) — resumes from the checkpoint on the next run.";
            r = new SyncResult { SyncType = logType, Error = log.ErrorMessage };
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = Trunc(ex.InnerException?.Message ?? ex.Message, 1800);
            r = new SyncResult { SyncType = logType, Error = log.ErrorMessage };
            _logger.LogError(ex, "{type} failed", logType);
        }

        r.SyncType = logType;
        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = r.RecordsFetched;
        log.RecordsInserted = r.RecordsInserted;
        log.RecordsUpdated  = r.RecordsUpdated;
        try { await db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("Could not write {type} sync-log row: {msg}", logType, ex.Message); }
        return r;
    }

    private DateTime HistoricalStartDate()
        => DateTime.TryParse(_config["SyncSettings:HistoricalStartDate"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date : new DateTime(2020, 1, 1);

    private static DateTime TodayIst() => DateTime.UtcNow.AddMinutes(330).Date;

    // ALWAYS dealer-wise. Fetching all dealers in one call makes the ERP itself throw OutOfMemoryException
    // (verified 14.11.2026: DJR 01-10-2026..31-10-2026, dealercode "" → 45 s, Valid=false, OutOfMemory;
    // dealercode CUS0379 → 3 s, 67 KB, OK). The all-dealer form is only possible with the explicit
    // escape hatch HistoricalSync:AllowAllDealerCalls = true (not recommended).
    private bool HistoricalPerDealer()
        => !_config.GetValue("HistoricalSync:AllowAllDealerCalls", false) || _config.GetValue("HistoricalSync:PerDealer", true);

    private async Task<List<string>> DealersForManualRangeAsync()
    {
        var d = HistoricalPerDealer() ? await GetSyncDealerCodesAsync(CancellationToken.None) : new List<string>();
        return d.Count > 0 ? d : new List<string> { "" };
    }

    // Historical sync from HistoricalStartDate → today, resuming from the checkpoints (used by the hosted
    // service after every start, and by POST api/admin/reload/start?mode=historical). deadlineUtc lets the
    // nightly sync take over at its scheduled time; the historical sync then continues on the next slice.
    public async Task<SyncResult> RunHistoricalCheckpointSyncAsync(DateTime? deadlineUtc, CancellationToken ct,
                                                                   IReadOnlyCollection<string>? apis = null)
    {
        var order = _config.GetSection("HistoricalSync:ApiOrder").Get<string[]>()?
            .Select(SyncApi.Normalize).Where(a => a != null).Select(a => a!).Distinct().ToArray();
        var list = (apis is { Count: > 0 } ? apis.ToArray() : (order is { Length: > 0 } ? order : SyncApi.All));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = new DmsSyncLog { SyncType = "HistoricalCheckpointSync", SyncDate = DateOnly.FromDateTime(TodayIst()), StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync(CancellationToken.None);

        SyncResult r;
        try
        {
            r = await RunCheckpointSyncAsync(new CheckpointRunOptions(
                    list, HistoricalStartDate(), TodayIst(), HistoricalPerDealer(), null, deadlineUtc, "Historical"), ct);
            log.Status = string.IsNullOrEmpty(r.Error) ? "Success" : "PartialFailure";
            log.ErrorMessage = r.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status = "Interrupted";
            log.ErrorMessage = "Stopped (app shutting down / recycle) — resumes from the checkpoint on the next start.";
            r = new SyncResult { SyncType = "HistoricalCheckpointSync", Error = log.ErrorMessage };
            log.CompletedAt = DateTime.UtcNow;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* best effort during shutdown */ }
            throw;
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = Trunc(ex.InnerException?.Message ?? ex.Message, 1800);
            r = new SyncResult { SyncType = "HistoricalCheckpointSync", Error = log.ErrorMessage };
            _logger.LogError(ex, "Historical checkpoint sync failed");
        }

        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = r.RecordsFetched;
        log.RecordsInserted = r.RecordsInserted;
        log.RecordsUpdated  = r.RecordsUpdated;
        try { await db.SaveChangesAsync(CancellationToken.None); }
        catch (Exception ex) { _logger.LogWarning("Could not write HistoricalCheckpointSync sync-log row: {msg}", ex.Message); }
        return r;
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
    
        // The data is gone, so the checkpoints must not claim "Completed" any more.
        await ResetCheckpointsAsync(null, null, null, null, failedOnly: false, CancellationToken.None);
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
    //  2. DEALERS    — dealer + call-centre dealer sync BEFORE the truncate (long step; DMS_Dealers
    //                  is not truncated), so a stopped process cannot leave the data tables empty here.
    //  3. TRUNCATE + RE-FETCH — ServiceHistory, VehicleSales, VehicleDispatches, LineOrderReport are
    //                  truncated, then each report is fetched; failed chunks/dealers are retried and
    //                  reported, never swallowed.
    //  4. VALIDATE   — every table must have rows. "Success" only when there were no failed
    //                  chunks AND no empty tables; otherwise "PartialFailure".
    //  Outcome is written to the sync log (DmsSyncLogs, SyncType = 'NightlyFullReload').
    // ─────────────────────────────────────────────────────────
    public async Task<SyncResult> RunNightlyFullReloadAsync(CancellationToken ct = default)
    {
        var overall   = new SyncResult { SyncType = "NightlyFullReload" };
        var problems  = new List<string>();

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

        var fetchedBy = new Dictionary<string, long>();
        var skippedAtStart = ErpApiService.MalformedRecordsDropped;
        string SkipNote()
        {
            var n = ErpApiService.MalformedRecordsDropped - skippedAtStart;
            return n > 0 ? $" | NOTE: {n} malformed ERP record(s) were skipped (see 'Malformed ERP record' warnings)" : "";
        }

        void Collect(string name, SyncResult r)
        {
            overall.RecordsFetched  += r.RecordsFetched;
            overall.RecordsInserted += r.RecordsInserted;
            overall.RecordsUpdated  += r.RecordsUpdated;
            fetchedBy[name] = fetchedBy.GetValueOrDefault(name) + r.RecordsFetched;
            if (!string.IsNullOrEmpty(r.Error)) problems.Add($"{name}: {r.Error}");
        }

        // Tells "the ERP sent nothing" apart from "rows were received but not saved".
        string EmptyWhy(string name) => fetchedBy.GetValueOrDefault(name) == 0
            ? $"{name} table is EMPTY after reload — ERP returned 0 rows (fetched=0): ERP/API side, not a DB write problem"
            : $"{name} table is EMPTY after reload — {fetchedBy[name]} rows were fetched but none saved: DB write problem, check 'Skipping' warnings";

        try
        {
            // 1 ── PREFLIGHT (nothing is changed until this passes)
            await Step("preflight: checking ERP login and report API");
            await WaitForErpHealthyAsync(end, ct);

            // 2 ── DEALERS: the per-pincode ERP sync (GET /V1/callcenter/pin, ~900 calls) is SKIPPED by
            //      default so the four reports start loading straight after the preflight. It runs only
            //      when DMS_Dealers is empty (LOR needs dealer codes) or Sync:RefreshDealers = true.
            //      DMS_Dealers is never truncated, so this step cannot lose report data either way.
            var dealersEmpty = !await db.DmsDealers.AnyAsync(CancellationToken.None);
            if (dealersEmpty || _config.GetValue("Sync:RefreshDealers", false))
            {
                await Step(dealersEmpty
                    ? "dealers (pincode sync) — DMS_Dealers is empty, required for dealer-wise sync"
                    : "dealers (pincode sync) — Sync:RefreshDealers=true");
                Collect("Dealers", await SyncAllDealersAsync());
                ct.ThrowIfCancellationRequested();

                await Step("call-centre dealers");
                Collect("CallCentreDealers", await SyncCallCentreDealersAsync());
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                _logger.LogInformation("Nightly full reload — dealer pincode sync skipped (Sync:RefreshDealers=false, DMS_Dealers has rows).");
            }

            // 3 ── NO TRUNCATE any more. The old TRUNCATE + reload-everything-in-one-go left the tables empty
            //      for hours, was killed half-way by IIS recycles (tables left empty) and loaded years of ERP data
            //      into memory (OutOfMemoryException, HTTP 503 from the overloaded ERP). "Full" now means:
            //      put every month back to Pending and re-check all of them month-wise / dealer-wise with UPSERT.
            //      Data stays visible the whole time and the run resumes from the checkpoint after a stop.
            await Step("re-checking every month from HistoricalStartDate (checkpoints reset, UPSERT, no truncate)");
            await ResetCheckpointsAsync(null, start, end, null, failedOnly: false, CancellationToken.None);
            var full = await RunCheckpointSyncAsync(new CheckpointRunOptions(
                SyncApi.All, start, end, HistoricalPerDealer(), null, null, "FullRecheck"), ct);
            Collect("AllReports", full);
            foreach (var name in new[] { "ServiceHistory", "VehicleSales", "VehicleDispatches", "LineOrderReport" })
                fetchedBy[name] = full.RecordsFetched;   // EmptyWhy() below: "fetched=0" vs "fetched but not saved"
            ct.ThrowIfCancellationRequested();

            // 4 ── VALIDATE
            await Step("validating row counts");
            var nService  = await db.DmsServiceHistories.CountAsync(CancellationToken.None);
            var nSales    = await db.DmsVehicleSales.CountAsync(CancellationToken.None);
            var nDispatch = await db.DmsVehicleDispatches.CountAsync(CancellationToken.None);
            var nLor      = await db.DmsLineOrderReports.CountAsync(CancellationToken.None);

            if (nService  == 0) problems.Add(EmptyWhy("ServiceHistory"));
            if (nSales    == 0) problems.Add(EmptyWhy("VehicleSales"));
            if (nDispatch == 0) problems.Add(EmptyWhy("VehicleDispatches"));
            if (nLor      == 0) problems.Add(EmptyWhy("LineOrderReport"));

            var counts = $"rows: ServiceHistory={nService}, VehicleSales={nSales}, VehicleDispatches={nDispatch}, LineOrderReport={nLor}";

            if (problems.Count == 0)
            {
                log.Status       = "Success";
                log.ErrorMessage = Trunc("OK — " + counts + SkipNote(), 1800);
                _logger.LogInformation("Nightly full reload complete: {fet} fetched, {ins} inserted. {counts}",
                    overall.RecordsFetched, overall.RecordsInserted, counts);
            }
            else
            {
                log.Status       = "PartialFailure";
                log.ErrorMessage = Trunc(string.Join(" | ", problems) + " || " + counts + SkipNote(), 1800);
                overall.Error    = log.ErrorMessage;
                _logger.LogError("Nightly full reload finished WITH PROBLEMS: {problems} || {counts}",
                    string.Join(" | ", problems), counts);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status = "Interrupted";
            log.ErrorMessage = "INTERRUPTED (app shutting down / IIS recycle). Nothing was deleted; the sync resumes from the checkpoint on the next run.";
            overall.Error = log.ErrorMessage;
            _logger.LogWarning("Nightly full reload interrupted: {msg}", log.ErrorMessage);
            throw;
        }
        catch (Exception ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            log.Status = "Failed";
            log.ErrorMessage = Trunc(
                "FAILED (nothing deleted — no truncate) — " + reason, 1800);
            overall.Error = log.ErrorMessage;
            _logger.LogError(ex, "Nightly full re-check failed (no data deleted)");
        }
        finally
        {
            log.CompletedAt     = DateTime.UtcNow;
            log.RecordsFetched  = overall.RecordsFetched;
            log.RecordsInserted = overall.RecordsInserted;
            log.RecordsUpdated  = overall.RecordsUpdated;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Could not write the NightlyFullReload sync-log row"); }
        }

        return overall;
    }

    // ─────────────────────────────────────────────────────────
    // ERP PROBE — READ-ONLY DIAGNOSTIC (writes nothing to the database)
    // Calls each ERP report API on windows of growing size and returns ONLY row counts, timings and
    // errors (no business data), so "ERP returned nothing" can be told apart from "ERP rejects long
    // ranges" from "ERP answers fast with an error body". Control: DJR (known to work).
    //   VSR / VDR : all dealers, last 7 / 30 / 90 / 365 days
    //   LOR       : the 3 most active dealers (by ServiceHistory rows), last 7 / 90 / 365 days and the FULL range
    // ─────────────────────────────────────────────────────────
    public sealed record ProbeRow(string Report, string Window, string From, string To, string Dealer,
                                  int HttpStatus, bool? Valid, int Rows, int BodyChars, long Ms,
                                  string? ErpDescription, string? TopLevelKeys, string? Error, string? RequestJson);

    public async Task<List<ProbeRow>> ProbeErpReportsAsync(CancellationToken ct = default)
    {
        var rows  = new List<ProbeRow>();
        var end   = DateTime.UtcNow.AddMinutes(330).Date;   // today in IST
        var full  = DateTime.Parse(_config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01").Date;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dealers = await db.DmsServiceHistories
            .Where(x => x.DealerCode != null)
            .GroupBy(x => x.DealerCode!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(3)
            .ToListAsync(ct);

        async Task Probe(string report, string window, DateTime a, DateTime b, string dealer)
        {
            ct.ThrowIfCancellationRequested();
            var token = await _erpApi.GetValidTokenAsync();
            var r = await _erpApi.ProbeReportAsync(report, dealer, a, b, token);
            rows.Add(new ProbeRow(report.ToUpperInvariant(), window, a.ToString("yyyy-MM-dd"), b.ToString("yyyy-MM-dd"),
                                  string.IsNullOrEmpty(dealer) ? "(all)" : dealer,
                                  r.HttpStatus, r.Valid, r.Rows, r.BodyChars, r.Ms, r.Description, r.TopLevelKeys, r.Error, r.RequestJson));
        }

        // Control: DJR, last 7 days (known to work).
        await Probe("djr", "last 7 days", end.AddDays(-6), end, "");

        foreach (var (name, days) in new[] { ("last 7 days", 6), ("last 30 days", 29), ("last 90 days", 89), ("last 365 days", 364) })
        {
            var a = end.AddDays(-days);
            await Probe("vsr", name, a, end, "");
            await Probe("vdr", name, a, end, "");
        }

        // VSR / VDR for ONE named dealer (the all-dealer call above uses an empty dealer code). If the
        // all-dealer call is refused but these work, the ERP only blocks the "all dealers" form.
        foreach (var dealer in dealers.Take(1))
        {
            await Probe("vsr", "last 30 days, one dealer", end.AddDays(-29), end, dealer);
            await Probe("vsr", "last 365 days, one dealer", end.AddDays(-364), end, dealer);
            await Probe("vdr", "last 30 days, one dealer", end.AddDays(-29), end, dealer);
            await Probe("vdr", "last 365 days, one dealer", end.AddDays(-364), end, dealer);
        }

        foreach (var dealer in dealers)
            foreach (var (name, a) in new[] { ("last 7 days", end.AddDays(-6)), ("last 90 days", end.AddDays(-89)),
                                              ("last 365 days", end.AddDays(-364)), ("full range", full) })
                await Probe("lor", name, a, end, dealer);

        return rows;
    }

    // ─────────────────────────────────────────────────────────
    // LOAD MISSING TABLES — NO TRUNCATE
    // Loads only the report tables that are EMPTY (or the ones you name), from
    // SyncSettings:HistoricalStartDate (or the given from/to) up to today (IST). Nothing is deleted:
    // the Backfill* methods are insert-only (rows that already exist are skipped), so a table that
    // already holds data — e.g. ServiceHistory with 4,59,465 rows — is left untouched and is not re-fetched.
    //
    //   onlyTables : any of "djr", "vsr", "vdr", "lor" (null/empty = every table that is empty now)
    //   from / to  : optional range (e.g. to repair two failed months after an ERP/parser fix)
    //
    // Dealers (pincode API) are called only if LOR is selected and DMS_Dealers is empty.
    // Outcome is written to the sync log (SyncType = 'MissingTablesLoad').
    // ─────────────────────────────────────────────────────────
    public async Task<SyncResult> RunMissingTablesLoadAsync(
        IReadOnlyCollection<string>? onlyTables = null, DateTime? from = null, DateTime? to = null,
        CancellationToken ct = default)
    {
        var overall  = new SyncResult { SyncType = "MissingTablesLoad" };
        var problems = new List<string>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "MissingTablesLoad", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var start = (from ?? DateTime.Parse(_config["SyncSettings:HistoricalStartDate"] ?? "2020-01-01")).Date;
        var end   = (to   ?? DateTime.UtcNow.AddMinutes(330).Date).Date;   // today in IST (UTC+5:30)
        var fetchedBy = new Dictionary<string, long>();
        var skippedAtStart = ErpApiService.MalformedRecordsDropped;
        string SkipNote()
        {
            var n = ErpApiService.MalformedRecordsDropped - skippedAtStart;
            return n > 0 ? $" | NOTE: {n} malformed ERP record(s) were skipped (see 'Malformed ERP record' warnings)" : "";
        }

        async Task Step(string text)
        {
            _logger.LogInformation("Missing-tables load — {step}", text);
            log.ErrorMessage = Trunc("Progress: " + text, 1800);
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning("Could not save load progress: {msg}", ex.Message); }
        }

        void Collect(string name, SyncResult r)
        {
            overall.RecordsFetched  += r.RecordsFetched;
            overall.RecordsInserted += r.RecordsInserted;
            fetchedBy[name] = fetchedBy.GetValueOrDefault(name) + r.RecordsFetched;
            if (!string.IsNullOrEmpty(r.Error)) problems.Add($"{name}: {r.Error}");
        }

        string EmptyWhy(string name) => fetchedBy.GetValueOrDefault(name) == 0
            ? $"{name} table is EMPTY — ERP returned 0 rows (fetched=0): ERP/API side, not a DB write problem"
            : $"{name} table is EMPTY — {fetchedBy[name]} rows were fetched but none saved: DB write problem, check 'Skipping' warnings";

        try
        {
            if (end < start)
                throw new InvalidOperationException($"Invalid range: to ({end:yyyy-MM-dd}) is before from ({start:yyyy-MM-dd}).");

            // Which tables to load
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (onlyTables != null && onlyTables.Count > 0)
            {
                foreach (var t in onlyTables)
                {
                    var k = t.Trim().ToLowerInvariant();
                    if (k is "djr" or "vsr" or "vdr" or "lor") want.Add(k);
                    else throw new InvalidOperationException($"Unknown table '{t}'. Use djr, vsr, vdr, lor.");
                }
            }
            else
            {
                if (!await db.DmsServiceHistories.AnyAsync(CancellationToken.None))    want.Add("djr");
                if (!await db.DmsVehicleSales.AnyAsync(CancellationToken.None))        want.Add("vsr");
                if (!await db.DmsVehicleDispatches.AnyAsync(CancellationToken.None))   want.Add("vdr");
                if (!await db.DmsLineOrderReports.AnyAsync(CancellationToken.None))    want.Add("lor");
            }

            if (want.Count == 0)
            {
                log.Status       = "Success";
                log.ErrorMessage = "Nothing to load — all four tables already have data.";
                _logger.LogInformation("Missing-tables load: nothing to do, all four tables have data.");
                return overall;
            }

            var label = string.Join(", ", new[] { "vsr", "vdr", "lor", "djr" }.Where(want.Contains).Select(x => x.ToUpperInvariant()));
            await Step($"loading {label} for {start:yyyy-MM-dd} → {end:yyyy-MM-dd} (no truncate); preflight: checking ERP");
            await WaitForErpHealthyAsync(end, ct);

            // Dealers only when LOR needs them and the table is empty.
            if (want.Contains("lor") && !await db.DmsDealers.AnyAsync(CancellationToken.None))
            {
                await Step("dealers (pincode sync) — DMS_Dealers is empty, required for LOR");
                Collect("Dealers", await SyncAllDealersAsync());
                ct.ThrowIfCancellationRequested();
            }

            // Faster reports first, slow ServiceHistory last.
            if (want.Contains("vsr"))
            {
                await Step($"VehicleSales {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
                Collect("VehicleSales", await BackfillVehicleSalesAsync(start, end, ct));
                ct.ThrowIfCancellationRequested();
            }
            if (want.Contains("vdr"))
            {
                await Step($"VehicleDispatches {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
                Collect("VehicleDispatches", await BackfillVehicleDispatchesAsync(start, end, ct));
                ct.ThrowIfCancellationRequested();
            }
            if (want.Contains("lor"))
            {
                await Step($"LineOrderReport {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
                Collect("LineOrderReport", await BackfillLineOrderReportAsync(start, end, ct));
                ct.ThrowIfCancellationRequested();
            }
            if (want.Contains("djr"))
            {
                await Step($"ServiceHistory {start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
                Collect("ServiceHistory", await BackfillHistoricalDataAsync(start, end, ct));
                ct.ThrowIfCancellationRequested();
            }

            // Validate: every table that was requested must have rows now.
            await Step("validating row counts");
            var nService  = await db.DmsServiceHistories.CountAsync(CancellationToken.None);
            var nSales    = await db.DmsVehicleSales.CountAsync(CancellationToken.None);
            var nDispatch = await db.DmsVehicleDispatches.CountAsync(CancellationToken.None);
            var nLor      = await db.DmsLineOrderReports.CountAsync(CancellationToken.None);

            if (want.Contains("djr") && nService  == 0) problems.Add(EmptyWhy("ServiceHistory"));
            if (want.Contains("vsr") && nSales    == 0) problems.Add(EmptyWhy("VehicleSales"));
            if (want.Contains("vdr") && nDispatch == 0) problems.Add(EmptyWhy("VehicleDispatches"));
            if (want.Contains("lor") && nLor      == 0) problems.Add(EmptyWhy("LineOrderReport"));

            var counts = $"loaded {label}, {start:yyyy-MM-dd}..{end:yyyy-MM-dd}; fetched={overall.RecordsFetched}, inserted={overall.RecordsInserted}; "
                       + $"rows: ServiceHistory={nService}, VehicleSales={nSales}, VehicleDispatches={nDispatch}, LineOrderReport={nLor}";

            if (problems.Count == 0)
            {
                log.Status       = "Success";
                log.ErrorMessage = Trunc("OK — " + counts + SkipNote(), 1800);
                _logger.LogInformation("Missing-tables load complete: {counts}", counts);
            }
            else
            {
                log.Status       = "PartialFailure";
                log.ErrorMessage = Trunc(string.Join(" | ", problems) + " || " + counts + SkipNote(), 1800);
                overall.Error    = log.ErrorMessage;
                _logger.LogError("Missing-tables load finished WITH PROBLEMS: {problems} || {counts}",
                    string.Join(" | ", problems), counts);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status       = "Interrupted";
            log.ErrorMessage = "INTERRUPTED (app shutting down). Nothing was deleted; run it again — rows already loaded are skipped.";
            overall.Error    = log.ErrorMessage;
            _logger.LogWarning("Missing-tables load interrupted: {msg}", log.ErrorMessage);
            throw;
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = Trunc("FAILED (nothing deleted): " + (ex.InnerException?.Message ?? ex.Message), 1800);
            overall.Error    = log.ErrorMessage;
            _logger.LogError(ex, "Missing-tables load failed");
        }
        finally
        {
            log.CompletedAt     = DateTime.UtcNow;
            log.RecordsFetched  = overall.RecordsFetched;
            log.RecordsInserted = overall.RecordsInserted;
            log.RecordsUpdated  = 0;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Could not write the MissingTablesLoad sync-log row"); }
        }

        return overall;
    }

    // ─────────────────────────────────────────────────────────
    // DAILY ROLLING-WINDOW SYNC — NO TRUNCATE
    // Fetches the last Sync:RollingMonths months (default 6) up to today (IST) for all four ERP
    // reports, date range by date range, and UPSERTS them: new rows are inserted, changed rows are
    // updated, nothing is deleted. The tables are never empty and older history is left alone, so
    // it is safe to run at any hour and a stopped process loses nothing.
    //
    //   ServiceHistory (DJR)  monthly windows        VehicleSales (VSR)       7-day windows
    //   VehicleDispatches     7-day windows          LineOrderReport (LOR)    per dealer, whole window
    //
    // A failed window is retried once; anything still failing is listed in the sync-log row
    // (SyncType = 'DailyWindowSync') and the run ends "PartialFailure" instead of a silent "Success".
    // Dealers are refreshed LAST (long pincode step) unless DMS_Dealers is empty, in which case they
    // are loaded first because LOR needs them.
    // ─────────────────────────────────────────────────────────
    public async Task<SyncResult> RunDailyWindowSyncAsync(CancellationToken ct = default)
    {
        var overall  = new SyncResult { SyncType = "DailyWindowSync" };
        var problems = new List<string>();
        var notes    = new List<string>();
        var skippedAtStart = ErpApiService.MalformedRecordsDropped;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "DailyWindowSync", StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var months = Math.Clamp(_config.GetValue("Sync:RollingMonths", 6), 1, 36);
        var end    = DateTime.UtcNow.AddMinutes(330).Date;   // today in IST (UTC+5:30)
        var start  = new DateTime(end.Year, end.Month, 1).AddMonths(-(months - 1));   // whole months

        async Task Step(string text)
        {
            _logger.LogInformation("Daily window sync — {step}", text);
            log.ErrorMessage = Trunc("Progress: " + text, 1800);
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning("Could not save sync progress: {msg}", ex.Message); }
        }

        void Add(SyncResult r)
        {
            overall.RecordsFetched  += r.RecordsFetched;
            overall.RecordsInserted += r.RecordsInserted;
            overall.RecordsUpdated  += r.RecordsUpdated;
        }

        try
        {
            await Step($"window {start:yyyy-MM-dd} → {end:yyyy-MM-dd} ({months} months), upsert, no truncate");

            // Dealers first ONLY when the table is empty (LOR needs them).
            var dealersEmpty = !await db.DmsDealers.AnyAsync(CancellationToken.None);
            if (dealersEmpty)
            {
                await Step("dealers (pincode sync) — DMS_Dealers was empty");
                var d = await SyncAllDealersAsync();
                Add(d);
                if (!string.IsNullOrEmpty(d.Error)) problems.Add("Dealers: " + d.Error);
                ct.ThrowIfCancellationRequested();
            }

            // Month-wise, dealer-wise UPSERT through the checkpoint engine. Months already refreshed by
            // tonight's run (completed after 00:00 IST today) are skipped, so a run that was stopped by an
            // IIS recycle / restart resumes where it stopped instead of starting again.
            //   Dealer-wise for every API (all-dealer month calls crash the ERP with OutOfMemoryException).
            var refreshCutoffUtc = end.AddMinutes(-330);   // 00:00 IST today, in UTC
            var nightly = await RunCheckpointSyncAsync(new CheckpointRunOptions(
                NightlyApiOrder, start, end,
                HistoricalPerDealer(),
                refreshCutoffUtc, null, "Nightly"), ct);
            Add(nightly);
            if (!string.IsNullOrEmpty(nightly.Error)) problems.Add(nightly.Error);
            ct.ThrowIfCancellationRequested();

            // Dealer refresh (GET /V1/callcenter/pin, ~900 calls) only when explicitly enabled.
            // OFF by default: the four reports are what the business needs every morning.
            if (_config.GetValue("Sync:RefreshDealers", false))
            {
                if (!dealersEmpty)
                {
                    ct.ThrowIfCancellationRequested();
                    await Step("dealers refresh (pincode sync)");
                    var d = await SyncAllDealersAsync();
                    Add(d);
                    if (!string.IsNullOrEmpty(d.Error)) notes.Add("Dealers refresh: " + d.Error);
                }
                ct.ThrowIfCancellationRequested();
                await Step("call-centre dealers refresh");
                var cc = await SyncCallCentreDealersAsync();
                Add(cc);
                if (!string.IsNullOrEmpty(cc.Error)) notes.Add("CallCentreDealers: " + cc.Error);
            }
            else
            {
                notes.Add("dealer pincode refresh skipped (Sync:RefreshDealers=false)");
            }

            // Validate
            await Step("validating row counts");
            var nService  = await db.DmsServiceHistories.CountAsync(CancellationToken.None);
            var nSales    = await db.DmsVehicleSales.CountAsync(CancellationToken.None);
            var nDispatch = await db.DmsVehicleDispatches.CountAsync(CancellationToken.None);
            var nLor      = await db.DmsLineOrderReports.CountAsync(CancellationToken.None);

            if (nService  == 0) problems.Add("ServiceHistory table is EMPTY");
            if (nSales    == 0) problems.Add("VehicleSales table is EMPTY");
            if (nDispatch == 0) problems.Add("VehicleDispatches table is EMPTY");
            if (nLor      == 0) problems.Add("LineOrderReport table is EMPTY");

            var counts = $"window {start:yyyy-MM-dd}..{end:yyyy-MM-dd}; fetched={overall.RecordsFetched}, inserted={overall.RecordsInserted}, updated={overall.RecordsUpdated}; "
                       + $"rows: ServiceHistory={nService}, VehicleSales={nSales}, VehicleDispatches={nDispatch}, LineOrderReport={nLor}";
            var skippedNow = ErpApiService.MalformedRecordsDropped - skippedAtStart;
            if (skippedNow > 0) notes.Add($"{skippedNow} malformed ERP record(s) were skipped (see 'Malformed ERP record' warnings)");
            var noteText = notes.Count > 0 ? " | NOTES: " + string.Join(" | ", notes) : "";

            if (problems.Count == 0)
            {
                log.Status       = "Success";
                log.ErrorMessage = Trunc("OK — " + counts + noteText, 1800);
                _logger.LogInformation("Daily window sync complete: {counts}", counts);
            }
            else
            {
                log.Status       = "PartialFailure";
                log.ErrorMessage = Trunc(string.Join(" | ", problems) + " || " + counts + noteText, 1800);
                overall.Error    = log.ErrorMessage;
                _logger.LogError("Daily window sync finished WITH PROBLEMS: {problems} || {counts}",
                    string.Join(" | ", problems), counts);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status       = "Interrupted";
            log.ErrorMessage = "INTERRUPTED (app shutting down). Nothing was deleted; the next run upserts the same window again.";
            overall.Error    = log.ErrorMessage;
            _logger.LogWarning("Daily window sync interrupted: {msg}", log.ErrorMessage);
            throw;
        }
        catch (Exception ex)
        {
            log.Status       = "Failed";
            log.ErrorMessage = Trunc("FAILED (nothing deleted): " + (ex.InnerException?.Message ?? ex.Message), 1800);
            overall.Error    = log.ErrorMessage;
            _logger.LogError(ex, "Daily window sync failed");
        }
        finally
        {
            log.CompletedAt     = DateTime.UtcNow;
            log.RecordsFetched  = overall.RecordsFetched;
            log.RecordsInserted = overall.RecordsInserted;
            log.RecordsUpdated  = overall.RecordsUpdated;
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Could not write the DailyWindowSync sync-log row"); }
        }

        return overall;
    }

    // Faster reports first, the slow ServiceHistory (DJR) last — a stop during DJR then cannot leave the
    // other three un-refreshed.
    private static readonly string[] NightlyApiOrder =
        { SyncApi.VehicleSales, SyncApi.VehicleDispatches, SyncApi.LineOrderReport, SyncApi.ServiceHistory };

    // Upsert variant of the DJR fallback: if a window fails, split it into halves (down to
    // Sync:DjrMinWindowDays) and upsert each piece. Returns a combined result; Error is set only if a
    // piece still fails at the minimum size. Upsert is idempotent, so repeating a window is safe.
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
                // One dealer, one day — an all-dealer call can itself crash the ERP (OutOfMemory).
                var probeDealer = (await GetSyncDealerCodesAsync(ct)).FirstOrDefault() ?? "";
                await _erpApi.FetchDjrAsync(probeDate, token, probeDealer);   // throws if the call fails after its own retries
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
        => await UpsertRangeViaEngineAsync(SyncApi.ServiceHistory, "ServiceHistoryUpsert", startDate, endDate, await DealersForManualRangeAsync(), null);

    public async Task<SyncResult> UpsertVehicleSalesForRangeAsync(DateTime startDate, DateTime endDate)
        => await UpsertRangeViaEngineAsync(SyncApi.VehicleSales, "VehicleSalesUpsert", startDate, endDate, await DealersForManualRangeAsync(), null);

    public async Task<SyncResult> UpsertVehicleDispatchesForRangeAsync(DateTime startDate, DateTime endDate)
        => await UpsertRangeViaEngineAsync(SyncApi.VehicleDispatches, "VehicleDispatchesUpsert", startDate, endDate, await DealersForManualRangeAsync(), null);

    // failedDealers (optional): receives the dealer codes whose ERP fetch failed, so a caller can retry
    // just those. onlyDealers (optional): restrict the run to these dealer codes. Both default to null,
    // which keeps the behaviour of the existing PUT endpoint unchanged.
    public async Task<SyncResult> UpsertLineOrderReportAsync(
        DateTime startDate, DateTime endDate,
        List<string>? failedDealers = null,
        IReadOnlyCollection<string>? onlyDealers = null)
    {
        var dealers = await GetSyncDealerCodesAsync(CancellationToken.None);
        if (onlyDealers != null)
            dealers = dealers.Where(c => onlyDealers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (dealers.Count == 0)
            return new SyncResult { SyncType = "LineOrderReportUpsert", Date = endDate,
                                    Error = "No dealers in DMS_Dealers. Run dealer sync first." };
        return await UpsertRangeViaEngineAsync(SyncApi.LineOrderReport, "LineOrderReportUpsert", startDate, endDate, dealers, failedDealers);
    }

    // On-demand UPSERT for a date range (PUT api/data/...). Same code path as the checkpoint sync, but
    // without checkpoints: the range is fetched month by month (never one huge ERP call), each month is
    // upserted in batches of 500 — new rows inserted, changed rows updated, unchanged rows left alone.
    private async Task<SyncResult> UpsertRangeViaEngineAsync(string api, string logType, DateTime startDate, DateTime endDate,
                                                             List<string> dealers, List<string>? failedDealers)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = new DmsSyncLog { SyncType = logType, SyncDate = DateOnly.FromDateTime(endDate), StartedAt = DateTime.UtcNow, Status = "Running" };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync();

        var result = new SyncResult { SyncType = logType, Date = endDate };
        var counts = new UpsertCounts();
        var errors = new List<string>();
        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            foreach (var dealer in dealers)
            {
                for (var ms = new DateTime(startDate.Year, startDate.Month, 1); ms <= endDate.Date; ms = ms.AddMonths(1))
                {
                    var a = ms < startDate.Date ? startDate.Date : ms;
                    var b = ms.AddMonths(1).AddDays(-1);
                    if (b > endDate.Date) b = endDate.Date;
                    try
                    {
                        await FetchAndUpsertWindowAsync(api, dealer, a, b, DateOnly.FromDateTime(b), dealer.Length > 0, token, counts, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not ErpAuthorizationException && !IsErpAuthError(ex.Message))
                    {
                        errors.Add($"{(dealer.Length == 0 ? "all dealers" : dealer)} {a:yyyy-MM-dd}..{b:yyyy-MM-dd}: {Trunc(ex.Message, 300)}");
                        if (dealer.Length > 0 && failedDealers != null && !failedDealers.Contains(dealer)) failedDealers.Add(dealer);
                    }
                }
            }
            log.Status = errors.Count == 0 ? "Success" : "PartialFailure";
            log.ErrorMessage = Trunc($"UPSERT: {counts.Unchanged} unchanged, {counts.Skipped} skipped"
                                     + (errors.Count > 0 ? $"; {errors.Count} window(s) failed: " + string.Join(" | ", errors) : ""), 1800);
            if (errors.Count > 0) result.Error = log.ErrorMessage;
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = Trunc(ex.InnerException?.Message ?? ex.Message, 1800);
            result.Error = log.ErrorMessage;
            _logger.LogError(ex, "{type} failed", logType);
        }

        result.RecordsFetched  = counts.Fetched;
        result.RecordsInserted = counts.Inserted;
        result.RecordsUpdated  = counts.Updated;
        log.CompletedAt     = DateTime.UtcNow;
        log.RecordsFetched  = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated  = result.RecordsUpdated;
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