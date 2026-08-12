using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VehicleSalesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataSyncService _sync;

    public VehicleSalesController(AppDbContext db, DataSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    // ── GET /api/vehiclesales ────────────────────────────────
    // Query params: from, to, dealerCode, chassisNo, page, pageSize
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? dealerCode,
        [FromQuery] string? chassisNo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsVehicleSales.AsQueryable();

        if (!string.IsNullOrEmpty(dealerCode))
            q = q.Where(v => v.DealerCode == dealerCode);

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(v => v.ChassisNo != null && v.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(v => v.InvoiceDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(v => v.InvoiceDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(v => v.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ── GET /api/vehiclesales/chassis/{chassisNo} ────────────
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
    {
        var records = await _db.DmsVehicleSales
            .Where(v => v.ChassisNo == chassisNo)
            .OrderByDescending(v => v.InvoiceDate)
            .ToListAsync();
        return Ok(records);
    }

    // ── GET /api/vehiclesales/dealer/{dealerCode} ────────────
    [HttpGet("dealer/{dealerCode}")]
    public async Task<IActionResult> GetByDealer(string dealerCode,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleSales.Where(v => v.DealerCode == dealerCode);

        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.InvoiceDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.InvoiceDate <= t);

        return Ok(await q.OrderByDescending(v => v.InvoiceDate).ToListAsync());
    }

    // ── GET /api/vehiclesales/summary ────────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsVehicleSales.AsQueryable();

        if (DateOnly.TryParse(from, out var f)) q = q.Where(v => v.InvoiceDate >= f);
        if (DateOnly.TryParse(to, out var t))   q = q.Where(v => v.InvoiceDate <= t);

        var summary = await q.GroupBy(v => v.DealerCode)
            .Select(g => new
            {
                DealerCode   = g.Key,
                DealerName   = g.Select(x => x.DealerName).FirstOrDefault(),
                TotalSales   = g.Count(),
                TotalRevenue = g.Sum(x => x.NetAmount),
                TotalFameII  = g.Sum(x => x.FameIi)
            })
            .OrderByDescending(x => x.TotalSales)
            .ToListAsync();

        return Ok(summary);
    }

    // ── POST /api/vehiclesales/sync/today ────────────────────
    [HttpPost("sync/today")]
    public async Task<IActionResult> SyncToday()
    {
        var result = await _sync.SyncVehicleSalesForDateAsync(DateTime.UtcNow.Date);
        return Ok(result);
    }

    // ── POST /api/vehiclesales/sync/date/{date} ──────────────
    [HttpPost("sync/date/{date}")]
    public async Task<IActionResult> SyncDate(string date)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest(new { error = "Use yyyy-MM-dd format." });

        var result = await _sync.SyncVehicleSalesForDateAsync(parsed);
        return Ok(result);
    }

    // ── POST /api/vehiclesales/sync/range ────────────────────
    [HttpPost("sync/range")]
    public async Task<IActionResult> SyncRange([FromBody] DateRangeRequest req)
    {
        if (!DateTime.TryParse(req.From, out var from) ||
            !DateTime.TryParse(req.To, out var to))
            return BadRequest(new { error = "Use yyyy-MM-dd for both dates." });

        var result = await _sync.SyncVehicleSalesForRangeAsync(from, to);
        return Ok(result);
    }

    [HttpPost("backfill")]
    public IActionResult StartBackfill([FromQuery] bool forceResync = false)
    {
        _ = Task.Run(() => _sync.BackfillVehicleSalesAsync(forceResync: forceResync));
        return Accepted(new { message = $"Full VSR backfill started (range-based, forceResync={forceResync}). Check /api/sync/status." });
    }

    // POST /api/sync/vsr/debug?dealerCode=CUS0420&date=2026-06-09
    [HttpPost("vsr/debug")]
    public async Task<IActionResult> DebugVsr(
        [FromQuery] string dealerCode,
        [FromQuery] string date,
        [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest("Use yyyy-MM-dd");

        var token   = await erpApi.GetValidTokenAsync();
        var records = await erpApi.FetchVsrAsync(dealerCode, parsed, parsed, token);

        return Ok(new
        {
            DealerCode   = dealerCode,
            Date         = date,
            TotalFetched = records.Count,
            Sample       = records.Take(3).Select(r => new
            {
                r.DealerName,   // ← check if this is null
                r.DealerCode,
                r.InvoiceNo,
                r.ChassisNo,
                r.NetAmount
            })
        });
    }

    // POST /api/sync/vsr/debug-raw?dealerCode=CUS0420&date=2026-06-09
    [HttpPost("vsr/debug-raw")]
    public async Task<IActionResult> DebugVsrRaw(
        [FromQuery] string dealerCode,
        [FromQuery] string date,
        [FromServices] ErpApiService erpApi)
    {
        if (!DateTime.TryParse(date, out var parsed))
            return BadRequest("Use yyyy-MM-dd");

        var token = await erpApi.GetValidTokenAsync();

        // Raw fetch — bypass all deserialization
        var url      = $"http://erpapi.autogeniuserp.com/V1/erpreport/vsr?ver=1.0";
        var vendorId = 14;

        var req = new
        {
            dealercode    = dealerCode,
            vendorid      = vendorId,
            startdate     = parsed.ToString("dd-MM-yyyy"),
            enddate       = parsed.ToString("dd-MM-yyyy"),
            subvendorcode = "",
            dealerStatus  = "1",
            aadharPanReq  = "0",
            fameReq       = "2"
        };

        using var http    = new System.Net.Http.HttpClient();
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
        {
            Content = new System.Net.Http.StringContent(
                Newtonsoft.Json.JsonConvert.SerializeObject(req),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {token}");

        var resp  = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var raw   = System.Text.Encoding.UTF8.GetString(bytes);

        return Ok(new
        {
            Length  = raw.Length,
            Preview = raw[..Math.Min(2000, raw.Length)]
        });
    }

    // ── POST /api/vehiclesales/backfill ──────────────────────
    // [HttpPost("backfill")]
    // public IActionResult StartBackfill()
    // {
    //     _ = Task.Run(() => _sync.BackfillVehicleSalesAsync());
    //     return Accepted(new { message = "Full VSR backfill started. Check /api/sync/status." });
    // }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<VehicleSaleRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.VehicleSale(r.DealerCode, r.InvoiceNo, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existingKeys = (await _db.DmsVehicleSales
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsVehicleSale>();
        var skipped = new List<string>();

        foreach (var r in deduped)
        {
            if (existingKeys.Contains(r.UniqueKey!)) { skipped.Add(r.UniqueKey!); continue; }
            toInsert.Add(MapVehicleSale(r));
        }

        if (toInsert.Count > 0) { await _db.DmsVehicleSales.AddRangeAsync(toInsert); await _db.SaveChangesAsync(); }
        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<VehicleSaleRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.VehicleSale(r.DealerCode, r.InvoiceNo, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existing = await _db.DmsVehicleSales.Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey)).ToListAsync();
        var lookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;
        foreach (var r in deduped)
        {
            if (lookup.TryGetValue(r.UniqueKey!, out var row)) { ApplyVehicleSale(row, r); row.UpdatedAt = DateTime.UtcNow; updated++; }
            else { _db.DmsVehicleSales.Add(MapVehicleSale(r)); inserted++; }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    private static DmsVehicleSale MapVehicleSale(VehicleSaleRecordDto r) => new()
    {
        DealerName = r.DealerName, DealerCode = r.DealerCode, InvoiceNo = r.InvoiceNo, InvoiceDate = r.InvoiceDate,
        Location = r.Location, LocCode = r.LocCode, LocationCity = r.LocationCity, CustDob = r.CustDob, Gender = r.Gender,
        SoldTo = r.SoldTo, AccountType = r.AccountType, PartyEmail = r.PartyEmail, CusMob = r.CusMob,
        Address1 = r.Address1, Address2 = r.Address2, City = r.City, State = r.State, ExecutiveName = r.ExecutiveName,
        Pin = r.Pin, ChassisNo = r.ChassisNo, MotorNo = r.MotorNo, Remarks = r.Remarks, ItemModel = r.ItemModel,
        Oemmodel = r.Oemmodel, ColorCode = r.ColorCode, VehicleType = r.VehicleType, VehicleGroup = r.VehicleGroup,
        Hsnsaccode = r.Hsnsaccode, SaleType = r.SaleType, FinancedBy = r.FinancedBy, FinAmount = r.FinAmount,
        ItemRate = r.ItemRate, InsuAmount = r.InsuAmount, RegnAmount = r.RegnAmount, AcsryAmount = r.AcsryAmount,
        PreGstdiscAmount = r.PreGstdiscAmount, DiscTypeName = r.DiscTypeName, PostGstdisc = r.PostGstdisc,
        FameIi = r.FameIi, StateFameIi = r.StateFameIi, Sgstper = r.Sgstper, Sgstamount = r.Sgstamount,
        Cgstper = r.Cgstper, Cgstamount = r.Cgstamount, Igstper = r.Igstper, Igstamount = r.Igstamount,
        NetAmount = r.NetAmount, ReferenceNo = r.ReferenceNo, BookingDate = r.BookingDate, TotalCount = r.TotalCount,
        Battery = r.Battery, BatteryChemical = r.BatteryChemical, BatteryCapacity = r.BatteryCapacity,
        BatteryMake = r.BatteryMake, ChargerNo = r.ChargerNo, ChargerNo2 = r.ChargerNo2, Converter = r.Converter,
        Vcu = r.Vcu, ControllerNo = r.ControllerNo, FameIirequired = r.FameIirequired, SegmentName = r.SegmentName,
        InstitutionalName = r.InstitutionalName, SchemeName = r.SchemeName, RowHash = r.RowHash, UniqueKey = r.UniqueKey,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void ApplyVehicleSale(DmsVehicleSale row, VehicleSaleRecordDto r)
    {
        row.DealerName = r.DealerName; row.DealerCode = r.DealerCode; row.InvoiceNo = r.InvoiceNo; row.InvoiceDate = r.InvoiceDate;
        row.Location = r.Location; row.LocCode = r.LocCode; row.LocationCity = r.LocationCity; row.CustDob = r.CustDob; row.Gender = r.Gender;
        row.SoldTo = r.SoldTo; row.AccountType = r.AccountType; row.PartyEmail = r.PartyEmail; row.CusMob = r.CusMob;
        row.Address1 = r.Address1; row.Address2 = r.Address2; row.City = r.City; row.State = r.State; row.ExecutiveName = r.ExecutiveName;
        row.Pin = r.Pin; row.ChassisNo = r.ChassisNo; row.MotorNo = r.MotorNo; row.Remarks = r.Remarks; row.ItemModel = r.ItemModel;
        row.Oemmodel = r.Oemmodel; row.ColorCode = r.ColorCode; row.VehicleType = r.VehicleType; row.VehicleGroup = r.VehicleGroup;
        row.Hsnsaccode = r.Hsnsaccode; row.SaleType = r.SaleType; row.FinancedBy = r.FinancedBy; row.FinAmount = r.FinAmount;
        row.ItemRate = r.ItemRate; row.InsuAmount = r.InsuAmount; row.RegnAmount = r.RegnAmount; row.AcsryAmount = r.AcsryAmount;
        row.PreGstdiscAmount = r.PreGstdiscAmount; row.DiscTypeName = r.DiscTypeName; row.PostGstdisc = r.PostGstdisc;
        row.FameIi = r.FameIi; row.StateFameIi = r.StateFameIi; row.Sgstper = r.Sgstper; row.Sgstamount = r.Sgstamount;
        row.Cgstper = r.Cgstper; row.Cgstamount = r.Cgstamount; row.Igstper = r.Igstper; row.Igstamount = r.Igstamount;
        row.NetAmount = r.NetAmount; row.ReferenceNo = r.ReferenceNo; row.BookingDate = r.BookingDate; row.TotalCount = r.TotalCount;
        row.Battery = r.Battery; row.BatteryChemical = r.BatteryChemical; row.BatteryCapacity = r.BatteryCapacity;
        row.BatteryMake = r.BatteryMake; row.ChargerNo = r.ChargerNo; row.ChargerNo2 = r.ChargerNo2; row.Converter = r.Converter;
        row.Vcu = r.Vcu; row.ControllerNo = r.ControllerNo; row.FameIirequired = r.FameIirequired; row.SegmentName = r.SegmentName;
        row.InstitutionalName = r.InstitutionalName; row.SchemeName = r.SchemeName; row.RowHash = r.RowHash;
    }
}
