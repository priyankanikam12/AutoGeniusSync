using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/shadowfax")]
public class ShadowfaxController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ShadowfaxController> _logger;

    public ShadowfaxController(AppDbContext db, ILogger<ShadowfaxController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────
    // GET /api/shadowfax/vehicles
    // Shadowfax-specific: Status, JobcardCreationDate,
    // CompletionDate (InvoiceDate), RepairType (JobCardType)
    // ──────────────────────────────────────────────────────
    [HttpGet("vehicles")]
    public async Task<IActionResult> GetVehicles(
        [FromQuery] string? chassisNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var query = _db.DmsLineOrderReports.AsQueryable();

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
                CompletionDate      = x.DocDate,    // InvoiceDate = job completion
                RepairType          = x.JobCardType, // e.g. "In Warranty Period"

                // Status derived from DocDate (invoice = complete)
                Status = x.DocDate != null    ? "Repair Complete"
                       : x.JobDate != null    ? "In Repair"
                       : "Not in Hub"
            })
            .ToListAsync();

        return Ok(new
        {
            Total    = total,
            Page     = page,
            PageSize = pageSize,
            Records  = records
        });
    }

    // ──────────────────────────────────────────────────────
    // GET /api/shadowfax/parts
    // Point 2: parts details per job/chassis
    // ──────────────────────────────────────────────────────
    [HttpGet("parts")]
    public async Task<IActionResult> GetParts(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? jobNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var query = _db.DmsLineOrderReports
            .Where(x => x.ItemType == "Parts" || x.ItemType == "Part")
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null &&
                                     x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(jobNo))
            query = query.Where(x => x.JobNo == jobNo);

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
            .Select(x => new
            {
                x.ChassisNo,
                x.JobNo,
                x.RegNo,
                x.Model,
                x.DealerCode,
                x.DealerName,
                x.JobDate,
                x.ItemName,
                x.ItemDescription,
                x.ItemType,
                x.Qty,
                x.Rate,
                x.Total,
                x.TotalAmount,
                x.SgstAmount,
                x.CgstAmount,
                x.IgstAmount,
                x.Discount
            })
            .ToListAsync();

        return Ok(new
        {
            Total    = total,
            Page     = page,
            PageSize = pageSize,
            Records  = records
        });
    }

    // ──────────────────────────────────────────────────────
    // GET /api/shadowfax/invoice-jobcard
    // Point 3: Invoice to Jobcard mapping
    // ──────────────────────────────────────────────────────
    [HttpGet("invoice-jobcard")]
    public async Task<IActionResult> GetInvoiceJobcardMapping(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? invoiceNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var query = _db.DmsLineOrderReports
            .Where(x => x.DocNo != null)
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null &&
                                     x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(invoiceNo))
            query = query.Where(x => x.DocNo == invoiceNo);

        if (from.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(from.Value);
            query = query.Where(x => x.DocDate >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = DateOnly.FromDateTime(to.Value);
            query = query.Where(x => x.DocDate <= toDate);
        }

        var total = await query.CountAsync();

        var records = await query
            .OrderByDescending(x => x.DocDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                InvoiceNo      = x.DocNo,
                InvoiceDate    = x.DocDate,
                InvoiceType    = x.DocType,
                JobNo          = x.JobNo,
                JobDate        = x.JobDate,
                x.ChassisNo,
                x.RegNo,
                x.Model,
                x.DealerCode,
                x.DealerName,
                x.PartyName,
                x.PartyMobile,
                x.JobCardType,
                x.TotalAmount
            })
            .Distinct()
            .ToListAsync();

        return Ok(new
        {
            Total    = total,
            Page     = page,
            PageSize = pageSize,
            Records  = records
        });
    }
}