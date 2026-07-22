using AutoGeniusSync.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServiceHistoryController : ControllerBase
{
    private readonly AppDbContext _db;

    public ServiceHistoryController(AppDbContext db)
    {
        _db = db;
    }

    // ── GET /api/servicehistory?from=2022-07-01&to=2022-07-31 ──
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? dealerCode,
        [FromQuery] string? chassisNo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var query = _db.DmsServiceHistories
            .Where(s => !s.IsRowTotal)
            .AsQueryable();

        if (!string.IsNullOrEmpty(dealerCode))
            query = query.Where(s => s.DealerCode == dealerCode);

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(s => s.ChassisNo != null && s.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            query = query.Where(s => s.JobDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            query = query.Where(s => s.JobDate <= t);

        var total = await query.CountAsync();
        var records = await query
            .OrderByDescending(s => s.JobDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ── GET /api/servicehistory/chassis/{no} ─────────────────
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
    {
        var records = await _db.DmsServiceHistories
            .Where(s => s.ChassisNo == chassisNo && !s.IsRowTotal)
            .OrderByDescending(s => s.JobDate)
            .ToListAsync();

        return Ok(records);
    }

    // ── GET /api/servicehistory/dealer/{code} ────────────────
    [HttpGet("dealer/{dealerCode}")]
    public async Task<IActionResult> GetByDealer(string dealerCode,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsServiceHistories
            .Where(s => s.DealerCode == dealerCode && !s.IsRowTotal);

        if (DateOnly.TryParse(from, out var f)) q = q.Where(s => s.JobDate >= f);
        if (DateOnly.TryParse(to, out var t)) q = q.Where(s => s.JobDate <= t);

        var records = await q.OrderByDescending(s => s.JobDate).ToListAsync();
        return Ok(records);
    }

    // ── GET /api/servicehistory/summary ──────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsServiceHistories.Where(s => !s.IsRowTotal).AsQueryable();

        if (DateOnly.TryParse(from, out var f)) q = q.Where(s => s.JobDate >= f);
        if (DateOnly.TryParse(to, out var t)) q = q.Where(s => s.JobDate <= t);

        var summary = await q.GroupBy(s => s.DealerCode)
            .Select(g => new
            {
                DealerCode   = g.Key,
                CompName     = g.Select(x => x.CompName).FirstOrDefault(),
                TotalJobs    = g.Count(),
                TotalRevenue = g.Sum(x => x.NetTotal)
            })
            .OrderByDescending(x => x.TotalJobs)
            .ToListAsync();

        return Ok(summary);
    }

    [HttpGet("shadowfax/vehicles")]
    public async Task<IActionResult> GetShadowfaxVehicleData(
        [FromQuery] string? chassisNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var shadowfaxChassis = await _db.DmsShadowfaxChassisMasters
            .Select(x => x.ChassisNo!)
            .ToListAsync();
        var chassisSet = new HashSet<string>(shadowfaxChassis);

        var query = _db.DmsServiceHistories
            .Where(x => !x.IsRowTotal && x.ChassisNo != null && chassisSet.Contains(x.ChassisNo))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null &&
                                    x.ChassisNo.Contains(chassisNo));

        if (from.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(from.Value);
            query = query.Where(x => x.JobDate >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = DateOnly.FromDateTime(to.Value);
            query = query.Where(x => x.JobDate <= toDate);
        }

        var total = await query.CountAsync();

        var records = await query
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ShadowfaxVehicleDto
            {
                ChassisNo           = x.ChassisNo,
                JobNo               = x.JobNo,
                RegNo               = x.RegNo,
                Model               = x.Model,
                JobcardCreationDate = x.JobDate,
                CompletionDate      = x.InvoiceDate,
                RepairType          = x.JobType,
                DealerCode          = x.DealerCode,
                DealerName          = x.CompName,      // CompName = dealer company name
                PartyName           = x.PartyName,
                MobileNumber        = x.MobileNumber,
                DocNo               = x.DocNo,
                DocType             = x.DocType,
                NetTotal            = x.NetTotal,
                Location            = x.Location,
                Status = x.InvoiceDate != null ? "Repair Complete"
                    : x.JobDate != null     ? "In Repair"
                    : "Not in Hub"
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }
}
