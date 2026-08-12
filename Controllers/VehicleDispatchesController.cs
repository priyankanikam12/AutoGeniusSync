using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;

namespace AutoGeniusSync.Controllers;

// ─────────────────────────────────────────────────────────────
// Vehicle Dispatches Controller
// ─────────────────────────────────────────────────────────────
[ApiController]
[Route("api/[controller]")]
public class VehicleDispatchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataSyncService _sync;

    public VehicleDispatchesController(AppDbContext db, DataSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    // GET /api/vehicledispatches?from=2026-01-01&to=2026-05-28
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? chassisNo,
        [FromQuery] string? vehicleStatus,
        [FromQuery] string? locationCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsVehicleDispatches.AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(v => v.ChassisNo != null && v.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(vehicleStatus))
            q = q.Where(v => v.VehicleStatus == vehicleStatus);

        if (!string.IsNullOrEmpty(locationCode))
            q = q.Where(v => v.LocationCode == locationCode);

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(v => v.SaleDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(v => v.SaleDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(v => v.SaleDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // GET /api/vehicledispatches/chassis/P6DSC12SPBB002162
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
        => Ok(await _db.DmsVehicleDispatches
            .Where(v => v.ChassisNo == chassisNo)
            .OrderByDescending(v => v.SaleDate)
            .ToListAsync());

    // GET /api/vehicledispatches/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleDispatches.AsQueryable();
        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.SaleDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.SaleDate <= t);

        var summary = await q.GroupBy(v => v.LocationCode)
            .Select(g => new
            {
                LocationCode  = g.Key,
                DealerName    = g.Select(x => x.DealerName).FirstOrDefault(),
                TotalDispatched = g.Count(),
                Sold          = g.Count(x => x.VehicleStatus == "Sold"),
                InStock       = g.Count(x => x.VehicleStatus != "Sold")
            })
            .OrderByDescending(x => x.TotalDispatched)
            .ToListAsync();

        return Ok(summary);
    }

    // POST /api/vehicledispatches/sync/today
    [HttpPost("sync/today")]
    public async Task<IActionResult> SyncToday()
        => Ok(await _sync.SyncVehicleDispatchesForDateAsync(DateTime.UtcNow.Date));

    // POST /api/vehicledispatches/sync/date/2026-05-28
    [HttpPost("sync/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd format." });
        return Ok(await _sync.SyncVehicleDispatchesForDateAsync(parsed));
    }

    // POST /api/vehicledispatches/sync/range
    [HttpPost("sync/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To, out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd for both dates." });

        var result = await _sync.SyncVehicleDispatchesForRangeAsync(from, to);
        return Ok(result);
    }

    [HttpPost("backfill")]
    public IActionResult StartBackfill([FromQuery] bool forceResync = false)
    {
        _ = Task.Run(() => _sync.BackfillVehicleDispatchesAsync(forceResync: forceResync));
        return Accepted(new { message = $"Full VDR backfill started (range-based, forceResync={forceResync})." });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<VehicleDispatchRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.VehicleDispatch(r.InvoiceNo, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existingKeys = (await _db.DmsVehicleDispatches
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsVehicleDispatch>();
        var skipped = new List<string>();

        foreach (var r in deduped)
        {
            if (existingKeys.Contains(r.UniqueKey!)) { skipped.Add(r.UniqueKey!); continue; }
            toInsert.Add(MapVehicleDispatch(r));
        }

        if (toInsert.Count > 0) { await _db.DmsVehicleDispatches.AddRangeAsync(toInsert); await _db.SaveChangesAsync(); }
        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<VehicleDispatchRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.VehicleDispatch(r.InvoiceNo, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existing = await _db.DmsVehicleDispatches.Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey)).ToListAsync();
        var lookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;
        foreach (var r in deduped)
        {
            if (lookup.TryGetValue(r.UniqueKey!, out var row)) { ApplyVehicleDispatch(row, r); row.UpdatedAt = DateTime.UtcNow; updated++; }
            else { _db.DmsVehicleDispatches.Add(MapVehicleDispatch(r)); inserted++; }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    private static DmsVehicleDispatch MapVehicleDispatch(VehicleDispatchRecordDto r) => new()
    {
        SaleDate = r.SaleDate, InvoiceNo = r.InvoiceNo, InvoiceDate = r.InvoiceDate, Location = r.Location,
        LocationCode = r.LocationCode, LocationCity = r.LocationCity, LocationStatus = r.LocationStatus,
        DealerName = r.DealerName, Zone = r.Zone, AreaOffice = r.AreaOffice, MfgYear = r.MfgYear,
        BrandName = r.BrandName, ModelCode = r.ModelCode, ColorCode = r.ColorCode, ChassisNo = r.ChassisNo,
        RegNo = r.RegNo, MotorNo = r.MotorNo, BatteryId = r.BatteryId, BatteryNo = r.BatteryNo,
        EcuSerialNo = r.EcuSerialNo, EcuImEi = r.EcuImEi, EcuBalMac = r.EcuBalMac, ImmoblizerNo = r.ImmoblizerNo,
        BikeSimId = r.BikeSimId, BikeMobileNo = r.BikeMobileNo, ChargerNo = r.ChargerNo, ControllerNo = r.ControllerNo,
        SoundbarSerialNo = r.SoundbarSerialNo, SoundbarBalMac = r.SoundbarBalMac, Voltage = r.Voltage,
        RegNumber = r.RegNumber, StartDate = r.StartDate, Tyre1 = r.Tyre1, Tyre2 = r.Tyre2,
        VehicleStatus = r.VehicleStatus, BookingId = r.BookingId, BillNo = r.BillNo, BillDate = r.BillDate,
        BillType = r.BillType, FinancerName = r.FinancerName, FinAmount = r.FinAmount, NameOfParty = r.NameOfParty,
        Address1 = r.Address1, Address2 = r.Address2, State = r.State, City = r.City, Pin = r.Pin,
        MobileNo = r.MobileNo, Email = r.Email, AppPush = r.AppPush, LeadId = r.LeadId, Vcu = r.Vcu,
        RowHash = r.RowHash, UniqueKey = r.UniqueKey, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void ApplyVehicleDispatch(DmsVehicleDispatch row, VehicleDispatchRecordDto r)
    {
        row.SaleDate = r.SaleDate; row.InvoiceNo = r.InvoiceNo; row.InvoiceDate = r.InvoiceDate; row.Location = r.Location;
        row.LocationCode = r.LocationCode; row.LocationCity = r.LocationCity; row.LocationStatus = r.LocationStatus;
        row.DealerName = r.DealerName; row.Zone = r.Zone; row.AreaOffice = r.AreaOffice; row.MfgYear = r.MfgYear;
        row.BrandName = r.BrandName; row.ModelCode = r.ModelCode; row.ColorCode = r.ColorCode; row.ChassisNo = r.ChassisNo;
        row.RegNo = r.RegNo; row.MotorNo = r.MotorNo; row.BatteryId = r.BatteryId; row.BatteryNo = r.BatteryNo;
        row.EcuSerialNo = r.EcuSerialNo; row.EcuImEi = r.EcuImEi; row.EcuBalMac = r.EcuBalMac; row.ImmoblizerNo = r.ImmoblizerNo;
        row.BikeSimId = r.BikeSimId; row.BikeMobileNo = r.BikeMobileNo; row.ChargerNo = r.ChargerNo; row.ControllerNo = r.ControllerNo;
        row.SoundbarSerialNo = r.SoundbarSerialNo; row.SoundbarBalMac = r.SoundbarBalMac; row.Voltage = r.Voltage;
        row.RegNumber = r.RegNumber; row.StartDate = r.StartDate; row.Tyre1 = r.Tyre1; row.Tyre2 = r.Tyre2;
        row.VehicleStatus = r.VehicleStatus; row.BookingId = r.BookingId; row.BillNo = r.BillNo; row.BillDate = r.BillDate;
        row.BillType = r.BillType; row.FinancerName = r.FinancerName; row.FinAmount = r.FinAmount; row.NameOfParty = r.NameOfParty;
        row.Address1 = r.Address1; row.Address2 = r.Address2; row.State = r.State; row.City = r.City; row.Pin = r.Pin;
        row.MobileNo = r.MobileNo; row.Email = r.Email; row.AppPush = r.AppPush; row.LeadId = r.LeadId; row.Vcu = r.Vcu;
        row.RowHash = r.RowHash;
    }
}


