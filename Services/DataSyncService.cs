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
    // SYNC VEHICLE SALES (VSR) for a date range, all dealers
    // Loops every dealer from DMS_Dealers table
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

            // Get all dealer codes from DMS_Dealers table
            var dealerCodes = await db.DmsDealers
                .Where(d => d.DealerCode != null)
                .Select(d => d.DealerCode!)
                .Distinct()
                .ToListAsync();

            _logger.LogInformation("VSR sync for {date}: {n} dealers to process",
                date.ToString("dd-MM-yyyy"), dealerCodes.Count);

            foreach (var dealerCode in dealerCodes)
            {
                try
                {
                    var sales = await _erpApi.FetchVsrAsync(dealerCode, date, date, token);
                    result.RecordsFetched += sales.Count;

                    foreach (var sale in sales)
                    {
                        if (string.IsNullOrEmpty(sale.InvoiceNo)) continue;

                        var parsed = ParseVehicleSale(sale);

                        // Upsert by (DealerCode, InvoiceNo)
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
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("VSR fetch failed for dealer {dc}: {msg}", dealerCode, ex.Message);
                }
            }

            log.Status = "Success";
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = ex.Message;
            result.Error = ex.Message;
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
    // HISTORICAL BACKFILL for Vehicle Sales
    // ─────────────────────────────────────────────────────────

    public async Task<SyncResult> BackfillVehicleSalesAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken ct = default)
    {
        var startDateStr = _config["SyncSettings:HistoricalStartDate"] ?? "2022-01-01";
        var start = fromDate ?? DateTime.Parse(startDateStr);
        var end   = toDate   ?? DateTime.UtcNow.Date;

        var totalResult = new SyncResult { SyncType = "BackfillVehicleSales" };
        _logger.LogInformation("VSR Backfill: {start} → {end}",
            start.ToShortDateString(), end.ToShortDateString());

        var current = start;
        while (current <= end && !ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool alreadySynced = await db.DmsSyncLogs.AnyAsync(l =>
                l.SyncType == "VehicleSales" &&
                l.SyncDate == DateOnly.FromDateTime(current) &&
                l.Status   == "Success");

            if (!alreadySynced)
            {
                var r = await SyncVehicleSalesForDateAsync(current);
                totalResult.RecordsFetched  += r.RecordsFetched;
                totalResult.RecordsInserted += r.RecordsInserted;
                totalResult.RecordsUpdated  += r.RecordsUpdated;
                _logger.LogInformation("VSR Backfill {date}: +{ins} inserted, +{upd} updated",
                    current.ToShortDateString(), r.RecordsInserted, r.RecordsUpdated);
            }
            else
            {
                _logger.LogDebug("VSR Backfill {date}: already done, skipping", current.ToShortDateString());
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
