using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

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
}
