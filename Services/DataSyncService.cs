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

            // Get pincodes: first from DB, then from appsettings
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

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
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

            // Fetch all jobs for this date (dealercode="" = all dealers)
            var jobs = await _erpApi.FetchDjrAsync(date, token, "");
            result.RecordsFetched = jobs.Count;

            _logger.LogInformation("Date {date}: fetched {n} records", date.ToString("dd-MM-yyyy"), jobs.Count);

            foreach (var job in jobs)
            {
                // Skip summary/total rows
                if (job.JobNo == "Total") continue;
                if (string.IsNullOrEmpty(job.DealerCode) && string.IsNullOrEmpty(job.JobNo)) continue;

                var parsed = ParseServiceHistory(job);

                // Upsert by (DealerCode, JobNo, JobDate)
                var existing = await db.DmsServiceHistories
                    .FirstOrDefaultAsync(x =>
                        x.DealerCode == parsed.DealerCode &&
                        x.JobNo == parsed.JobNo &&
                        x.JobDate == parsed.JobDate);

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

            await db.SaveChangesAsync();
            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error = ex.Message;
            _logger.LogError(ex, "Service history sync failed for {date}", date.ToShortDateString());
        }

        log.CompletedAt = DateTime.UtcNow;
        log.RecordsFetched = result.RecordsFetched;
        log.RecordsInserted = result.RecordsInserted;
        log.RecordsUpdated = result.RecordsUpdated;
        await db.SaveChangesAsync();

        return result;
    }

    // ─────────────────────────────────────────────────────────
    // HISTORICAL BACKFILL: from configured start date to today
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> BackfillHistoricalDataAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end = toDate ?? DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillHistorical" };
        _logger.LogInformation("Backfill: {start} → {end}", start.ToShortDateString(), end.ToShortDateString());

        var current = start;
        while (current <= end && !ct.IsCancellationRequested)
        {
            // Skip if already synced for this date
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadySynced = await db.DmsSyncLogs.AnyAsync(l =>
                l.SyncType == "ServiceHistory" &&
                l.SyncDate == DateOnly.FromDateTime(current) &&
                l.Status == "Success");

            if (!alreadySynced)
            {
                var r = await SyncServiceHistoryForDateAsync(current);
                totalResult.RecordsFetched += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated += r.RecordsUpdated;
                _logger.LogInformation("Backfill {date}: +{ins} inserted, +{upd} updated",
                    current.ToShortDateString(), r.RecordsInserted, r.RecordsUpdated);
            }
            else
            {
                _logger.LogDebug("Backfill {date}: already synced, skipping", current.ToShortDateString());
            }

            current = current.AddDays(1);
        }

        return totalResult;
    }

    // ─────────────────────────────────────────────────────────
    // MAPPING HELPERS
    // ─────────────────────────────────────────────────────────

    private static DmsDealer MapDealer(DealerValue d) => new()
    {
        DealerCode          = d.DealerCode,
        DealerCompany       = d.DealerCompany,
        ContactNo           = d.ContactNo,
        AlternateContactNo  = d.AlternateContactNo,
        DealerStateName     = d.DealerStateName,
        DealerCityName      = d.DealerCityName,
        PinCode             = d.PinCode,
        ActiveStatus        = d.ActiveStatus,
        LastFetchedAt       = DateTime.UtcNow,
        CreatedAt           = DateTime.UtcNow,
        UpdatedAt           = DateTime.UtcNow
    };

    private static void UpdateDealer(DmsDealer existing, DealerValue d)
    {
        existing.DealerCompany      = d.DealerCompany;
        existing.ContactNo          = d.ContactNo;
        existing.AlternateContactNo = d.AlternateContactNo;
        existing.DealerStateName    = d.DealerStateName;
        existing.DealerCityName     = d.DealerCityName;
        existing.PinCode            = d.PinCode;
        existing.ActiveStatus       = d.ActiveStatus;
        existing.LastFetchedAt      = DateTime.UtcNow;
        existing.UpdatedAt          = DateTime.UtcNow;
    }

    private static DmsServiceHistory ParseServiceHistory(DjrValue j)
    {
        return new DmsServiceHistory
        {
            DealerCode              = j.DealerCode,
            JobNo                   = j.JobNo,
            JobDate                 = ParseDate(j.JobDate),
            CompName                = j.CompName,
            Location                = j.Location,
            InTime                  = j.InTime,
            CloseTime               = j.CloseTime,
            JobCategory             = j.JobCategory,
            Ffrpercentage           = j.FFRPercentage,
            DocNo                   = j.DocNo,
            DocType                 = j.DocType,
            DocDate                 = ParseDate(j.DocDate),
            Model                   = j.Model,
            BrandName               = j.BrandName,
            RegNo                   = j.RegNo,
            VehicleType             = j.VehicleType,
            EngineNo                = j.EngineNo,
            ChassisNo               = j.ChassisNo,
            Kms                     = j.KMS,
            BatterySerialNo1        = j.BatterySerialNo1,
            BatterySerialNo2        = j.BatterySerialNo2,
            BatterySerialNo3        = j.BatterySerialNo3,
            BatterySerialNo4        = j.BatterySerialNo4,
            BatterySerialNo5        = j.BatterySerialNo5,
            BatterySerialNo6        = j.BatterySerialNo6,
            IndividualAhbattery1    = j.IndividualAHBattery1,
            IndividualAhbattery2    = j.IndividualAHBattery2,
            IndividualAhbattery3    = j.IndividualAHBattery3,
            IndividualAhbattery4    = j.IndividualAHBattery4,
            IndividualAhbattery5    = j.IndividualAHBattery5,
            IndividualAhbattery6    = j.IndividualAHBattery6,
            PartyName               = j.PartyName,
            MobileNumber            = j.MobileNumber,
            Supervisor              = j.Supervisor,
            Technician              = j.Technician,
            ServiceHead             = j.ServiceHead,
            JobType                 = j.JobType,
            SaleDate                = ParseDate(j.SaleDate),
            CouponNo                = j.CouponNo,
            ExpectedDeliveryDate    = ParseDate(j.ExpectedDeliveryDate),
            ProformaDate            = ParseDate(j.ProformaDate),
            InvoiceDate             = ParseDate(j.InvoiceDate),
            EstimatedJobExpenses    = ParseDecimal(j.EstimatedJobExpenses),
            LabourHours             = ParseDecimal(j.LabourHours),
            Parts                   = ParseDecimal(j.Parts),
            Accessory               = ParseDecimal(j.Accessory),
            Oil                     = ParseDecimal(j.Oil),
            Labour                  = ParseDecimal(j.Labour),
            OutsideWork             = ParseDecimal(j.OutsideWork),
            TotalWotax              = ParseDecimal(j.TotalWOTax),
            Gstamount               = ParseDecimal(j.GSTAmount),
            Igstamount              = ParseDecimal(j.IGSTAmount),
            NetTotal                = ParseDecimal(j.NetTotal),
            IsRowTotal              = j.JobNo == "Total",
            CreatedAt               = DateTime.UtcNow,
            UpdatedAt               = DateTime.UtcNow
        };
    }

    private static void UpdateServiceHistory(DmsServiceHistory e, DmsServiceHistory n)
    {
        e.CompName = n.CompName; e.Location = n.Location; e.InTime = n.InTime;
        e.CloseTime = n.CloseTime; e.JobCategory = n.JobCategory;
        e.Ffrpercentage = n.Ffrpercentage; e.DocNo = n.DocNo;
        e.DocType = n.DocType; e.DocDate = n.DocDate; e.Model = n.Model;
        e.BrandName = n.BrandName; e.RegNo = n.RegNo; e.VehicleType = n.VehicleType;
        e.EngineNo = n.EngineNo; e.ChassisNo = n.ChassisNo; e.Kms = n.Kms;
        e.BatterySerialNo1 = n.BatterySerialNo1; e.BatterySerialNo2 = n.BatterySerialNo2;
        e.BatterySerialNo3 = n.BatterySerialNo3; e.BatterySerialNo4 = n.BatterySerialNo4;
        e.BatterySerialNo5 = n.BatterySerialNo5; e.BatterySerialNo6 = n.BatterySerialNo6;
        e.IndividualAhbattery1 = n.IndividualAhbattery1;
        e.IndividualAhbattery2 = n.IndividualAhbattery2;
        e.PartyName = n.PartyName; e.MobileNumber = n.MobileNumber;
        e.Supervisor = n.Supervisor; e.Technician = n.Technician;
        e.ServiceHead = n.ServiceHead; e.JobType = n.JobType;
        e.SaleDate = n.SaleDate; e.CouponNo = n.CouponNo;
        e.ExpectedDeliveryDate = n.ExpectedDeliveryDate;
        e.InvoiceDate = n.InvoiceDate;
        e.EstimatedJobExpenses = n.EstimatedJobExpenses;
        e.LabourHours = n.LabourHours; e.Parts = n.Parts;
        e.Accessory = n.Accessory; e.Oil = n.Oil; e.Labour = n.Labour;
        e.OutsideWork = n.OutsideWork; e.TotalWotax = n.TotalWotax;
        e.Gstamount = n.Gstamount; e.Igstamount = n.Igstamount;
        e.NetTotal = n.NetTotal; e.UpdatedAt = DateTime.UtcNow;
    }

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        // Try dd-MM-yyyy first, then other formats
        if (DateTime.TryParseExact(val, "dd-MM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        if (DateTime.TryParse(val, out var d2)) return DateOnly.FromDateTime(d2);
        return null;
    }

    private static decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        return decimal.TryParse(val, out var d) ? d : 0;
    }
}
