using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProformaController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ProformaController> _logger;

    public ProformaController(AppDbContext db, ILogger<ProformaController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── GET /api/proforma?invoiceNo=...&chassisNo=...&rBillNo=...&from=...&to=... ──
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? invoiceNo,
        [FromQuery] string? chassisNo,
        [FromQuery] string? rBillNo,
        [FromQuery] string? serialNo,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsProformas.AsQueryable();

        if (!string.IsNullOrEmpty(invoiceNo))
            q = q.Where(x => x.InvoiceNo == invoiceNo);

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(rBillNo))
            q = q.Where(x => x.RBillNo == rBillNo);

        if (!string.IsNullOrEmpty(serialNo))
            q = q.Where(x => x.SerialNo.Contains(serialNo));

        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f))
            q = q.Where(x => x.InvoiceDate >= f);

        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t))
            q = q.Where(x => x.InvoiceDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(x => x.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ── GET /api/proforma/{serialNo} ──
    [HttpGet("{serialNo}")]
    public async Task<IActionResult> GetOne(string serialNo)
    {
        var record = await _db.DmsProformas.FirstOrDefaultAsync(x => x.SerialNo == serialNo);
        return record == null ? NotFound() : Ok(record);
    }

    // ── GET /api/proforma/chassis/{chassisNo} ──
    [HttpGet("chassis/{chassisNo}")]
    public async Task<IActionResult> GetByChassis(string chassisNo)
        => Ok(await _db.DmsProformas
            .Where(x => x.ChassisNo == chassisNo)
            .OrderByDescending(x => x.InvoiceDate)
            .ToListAsync());

    // ── POST /api/proforma — insert only, 409 if SerialNo exists ──
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProformaDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.SerialNo))
            return BadRequest(new { error = "Serial No is required." });

        var existing = await _db.DmsProformas.FirstOrDefaultAsync(x => x.SerialNo == dto.SerialNo);
        if (existing != null)
            return Conflict(new { error = $"Proforma with Serial No {dto.SerialNo} already exists (unique key: SerialNo). Use PUT to update." });

        var entity = MapToEntity(dto);
        entity.CreatedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        _db.DmsProformas.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Proforma created: {serial}", dto.SerialNo);
        return CreatedAtAction(nameof(GetOne), new { serialNo = dto.SerialNo }, entity);
    }

    // ── PUT /api/proforma — upsert by SerialNo ──
    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] ProformaDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.SerialNo))
            return BadRequest(new { error = "Serial No is required." });

        var existing = await _db.DmsProformas.FirstOrDefaultAsync(x => x.SerialNo == dto.SerialNo);
        bool created = existing == null;

        if (existing == null)
        {
            existing = MapToEntity(dto);
            existing.CreatedAt = DateTime.UtcNow;
            _db.DmsProformas.Add(existing);
        }
        else
        {
            var updated = MapToEntity(dto);
            existing.InvoiceNo       = updated.InvoiceNo;
            existing.InvoiceDate     = updated.InvoiceDate;
            existing.DealerName      = updated.DealerName;
            existing.DealerLocation  = updated.DealerLocation;
            existing.ModelName       = updated.ModelName;
            existing.ChassisNo       = updated.ChassisNo;
            existing.ItemCode        = updated.ItemCode;
            existing.ItemDescription = updated.ItemDescription;
            existing.RBillNo         = updated.RBillNo;
            existing.RBillDate       = updated.RBillDate;
            existing.PartyName       = updated.PartyName;
            existing.PartyState      = updated.PartyState;
        }

        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Proforma {action}: {serial}",
            created ? "created" : "updated", dto.SerialNo);

        return Ok(existing);
    }

    private static DmsProforma MapToEntity(ProformaDto dto) => new()
    {
        InvoiceNo       = dto.InvoiceNo,
        InvoiceDate     = ParseDate(dto.InvoiceDate),
        DealerName      = dto.DealerName,
        DealerLocation  = dto.DealerLocation,
        ModelName       = dto.ModelName,
        ChassisNo       = dto.ChassisNo,
        ItemCode        = dto.ItemCode,
        ItemDescription = dto.ItemDescription,
        SerialNo        = dto.SerialNo,
        RBillNo         = dto.RBillNo,
        RBillDate       = ParseDate(dto.RBillDate),
        PartyName       = dto.PartyName,
        PartyState      = dto.PartyState
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
}