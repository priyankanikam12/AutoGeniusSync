using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RepairBillController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<RepairBillController> _logger;

    public RepairBillController(AppDbContext db, ILogger<RepairBillController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── GET /api/repairbill?location=...&billNo=...&chassisNo=...&from=...&to=... ──
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? location,
        [FromQuery] string? billNo,
        [FromQuery] string? chassisNo,
        [FromQuery] string? jobNo,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsRepairBills.AsQueryable();

        if (!string.IsNullOrEmpty(location))
            q = q.Where(x => x.Location.Contains(location));

        if (!string.IsNullOrEmpty(billNo))
            q = q.Where(x => x.BillNo == billNo);

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(jobNo))
            q = q.Where(x => x.JobNo == jobNo);

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(x => x.BillDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(x => x.BillDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(x => x.BillDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ── GET /api/repairbill/{location}/{billNo} ──
    [HttpGet("{location}/{billNo}")]
    public async Task<IActionResult> GetOne(string location, string billNo)
    {
        var record = await _db.DmsRepairBills
            .FirstOrDefaultAsync(x => x.Location == location && x.BillNo == billNo);

        return record == null ? NotFound() : Ok(record);
    }

    // ── GET /api/repairbill/chassis/{chassisNo} ──
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
        => Ok(await _db.DmsRepairBills
            .Where(x => x.ChassisNo == chassisNo)
            .OrderByDescending(x => x.BillDate)
            .ToListAsync());

    // ── POST /api/repairbill — insert only, 409 if (Location, BillNo) exists ──
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RepairBillDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.BillNo) || string.IsNullOrWhiteSpace(dto.Location))
            return BadRequest(new { error = "BillNo and Location are required." });

        var existing = await _db.DmsRepairBills
            .FirstOrDefaultAsync(x => x.Location == dto.Location && x.BillNo == dto.BillNo);

        if (existing != null)
            return Conflict(new { error = $"Repair bill {dto.BillNo} at {dto.Location} already exists (unique key: Location + BillNo). Use PUT to update." });

        var entity = MapToEntity(dto);
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        _db.DmsRepairBills.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Repair bill created: {loc}/{bill}", dto.Location, dto.BillNo);
        return CreatedAtAction(nameof(GetOne), new { location = dto.Location, billNo = dto.BillNo }, entity);
    }

    // ── PUT /api/repairbill — upsert by (Location, BillNo) ──
    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] RepairBillDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.BillNo) || string.IsNullOrWhiteSpace(dto.Location))
            return BadRequest(new { error = "BillNo and Location are required." });

        var existing = await _db.DmsRepairBills
            .FirstOrDefaultAsync(x => x.Location == dto.Location && x.BillNo == dto.BillNo);

        bool created = existing == null;

        if (existing == null)
        {
            existing = MapToEntity(dto);
            existing.CreatedAt = DateTime.UtcNow;
            _db.DmsRepairBills.Add(existing);
        }
        else
        {
            var updated = MapToEntity(dto);
            existing.BillDate     = updated.BillDate;
            existing.PartyName    = updated.PartyName;
            existing.RegNo        = updated.RegNo;
            existing.BillType     = updated.BillType;
            existing.JobNo        = updated.JobNo;
            existing.NetAmount    = updated.NetAmount;
            existing.UserName     = updated.UserName;
            existing.UserNameEdit = updated.UserNameEdit;
            existing.DateAdded    = updated.DateAdded;
            existing.DateModified = updated.DateModified;
            existing.ChassisNo    = updated.ChassisNo;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Repair bill {action}: {loc}/{bill}",
            created ? "created" : "updated", dto.Location, dto.BillNo);

        return Ok(existing);
    }

    private static DmsRepairBill MapToEntity(RepairBillDto dto) => new()
    {
        BillDate     = ParseDate(dto.Date),
        BillNo       = dto.BillNo,
        Location     = dto.Location,
        PartyName    = dto.PartyName,
        RegNo        = dto.RegNo,
        BillType     = dto.BillType,
        JobNo        = dto.JobNo,
        NetAmount    = ParseDecimal(dto.NetAmount),
        UserName     = dto.UserName,
        UserNameEdit = dto.UserNameEdit,
        DateAdded    = ParseDateTime(dto.DateAdded),
        DateModified = ParseDateTime(dto.DateModified),
        ChassisNo    = dto.ChassisNo
    };

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

    private static DateTime? ParseDateTime(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        return DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static decimal? ParseDecimal(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        return decimal.TryParse(val, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}