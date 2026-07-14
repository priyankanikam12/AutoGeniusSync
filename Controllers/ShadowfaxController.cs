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
    private readonly IConfiguration _config;
    private readonly ILogger<ShadowfaxController> _logger;

    public ShadowfaxController(AppDbContext db, IConfiguration config,
        ILogger<ShadowfaxController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ── Helper: load Shadowfax dealer codes from config ───
    private List<string> GetShadowfaxDealerCodes()
        => _config.GetSection("ShadowfaxSettings:DealerCodes")
                  .Get<List<string>>() ?? new();

    // private async Task<List<string>> GetShadowfaxDealerCodes()
    // {
    //     // Get all Shadowfax pincodes
    //     var pincodes = await _db.DmsPincodeMasters
    //         .Where(x => !string.IsNullOrEmpty(x.PinCode))
    //         .Select(x => x.PinCode!)
    //         .Distinct()
    //         .ToListAsync();

    //     if (!pincodes.Any())
    //         return new List<string>();

    //     // Get dealer codes whose pincode matches
    //     return await _db.DmsDealers
    //         .Where(x => x.PinCode != null &&
    //                     pincodes.Contains(x.PinCode) &&
    //                     x.ActiveStatus == "Active")
    //         .Select(x => x.DealerCode!)
    //         .Distinct()
    //         .ToListAsync();
    // }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/vehicles
    // ─────────────────────────────────────────────────────
    [HttpGet("vehicles")]
    public async Task<IActionResult> GetVehicles(
        [FromQuery] string? chassisNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var sfxDealers =  GetShadowfaxDealerCodes();

        // ── If no Shadowfax dealer codes are configured, warn loudly
        // instead of silently returning every dealer's data ──────
        if (!sfxDealers.Any())
        {
            _logger.LogWarning(
                "ShadowfaxSettings:DealerCodes is empty — /api/shadowfax/vehicles " +
                "is returning ALL dealers, not just Shadowfax ones. " +
                "Use GET /api/dealers/search?name=... to find the correct codes " +
                "and add them to appsettings.json.");
        }

        var query = _db.DmsLineOrderReports.AsQueryable();

        if (sfxDealers.Any())
            query = query.Where(x => x.DealerCode != null &&
                                     sfxDealers.Contains(x.DealerCode));

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
                CompletionDate      = x.DocDate,
                RepairType          = x.JobCardType,
                DealerCode          = x.DealerCode,
                DealerName          = x.DealerName,
                PartyName           = x.PartyName,
                MobileNumber        = x.PartyMobile,
                DocNo               = x.DocNo,
                DocType             = x.DocType,
                NetTotal            = x.TotalAmount,   // ← FIX: was never mapped before
                Location            = x.Location,
                PaymentMode         = x.PaymentMode,
                Status = x.DocDate != null ? "Repair Complete"
                       : x.JobDate != null ? "In Repair"
                       : "Not in Hub"
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // NEW: GET /api/shadowfax/vehicles/invoiced
    // A separate, invoice-stage-only view — one row per
    // (chassis, invoice), not one row per LOR line item.
    // This directly answers point 3: "a separate table of
    // vehicles reaching invoice stage."
    // ─────────────────────────────────────────────────────
    [HttpGet("vehicles/invoiced")]
    public async Task<IActionResult> GetInvoicedVehicles(
        [FromQuery] string? chassisNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var sfxDealers = GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => x.DocNo != null && x.ChassisNo != null) // invoice raised
            .AsQueryable();

        if (sfxDealers.Any())
            query = query.Where(x => x.DealerCode != null &&
                                     sfxDealers.Contains(x.DealerCode));

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo!.Contains(chassisNo));

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

        // Collapse multiple line items down to one row per
        // (ChassisNo, DocNo) — pick the row with the highest
        // TotalAmount as the representative header-level row,
        // since header fields (DealerName/PartyName/etc.) repeat
        // identically across all line items for the same invoice.
        var grouped = await query
            .GroupBy(x => new { x.ChassisNo, x.DocNo })
            .Select(g => new
            {
                g.Key.ChassisNo,
                g.Key.DocNo,
                InvoiceDate  = g.Max(x => x.DocDate),
                JobNo        = g.Select(x => x.JobNo).FirstOrDefault(),
                JobDate      = g.Min(x => x.JobDate),
                RegNo        = g.Select(x => x.RegNo).FirstOrDefault(),
                Model        = g.Select(x => x.Model).FirstOrDefault(),
                DealerCode   = g.Select(x => x.DealerCode).FirstOrDefault(),
                DealerName   = g.Select(x => x.DealerName).FirstOrDefault(),
                PartyName    = g.Select(x => x.PartyName).FirstOrDefault(),
                MobileNumber = g.Select(x => x.PartyMobile).FirstOrDefault(),
                DocType      = g.Select(x => x.DocType).FirstOrDefault(),
                Location     = g.Select(x => x.Location).FirstOrDefault(),
                NetTotal     = g.Sum(x => x.TotalAmount)   // sum of all line items = invoice total
            })
            .OrderByDescending(x => x.InvoiceDate)
            .ToListAsync();

        var total = grouped.Count;
        var page_ = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = page_ });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/parts
    // ─────────────────────────────────────────────────────
    [HttpGet("parts")]
    public async Task<IActionResult> GetParts(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? jobNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var sfxDealers =  GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => !string.IsNullOrEmpty(x.ItemType))
            .AsQueryable();

        if (sfxDealers.Any())
            query = query.Where(x => x.DealerCode != null &&
                                     sfxDealers.Contains(x.DealerCode));

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
                x.PartyName,
                PartyMobile  = x.PartyMobile,
                x.JobDate,
                InvoiceNo    = x.DocNo,
                InvoiceDate  = x.DocDate,
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

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/invoice-jobcard
    // ─────────────────────────────────────────────────────
    [HttpGet("invoice-jobcard")]
    public async Task<IActionResult> GetInvoiceJobcardMapping(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? invoiceNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var sfxDealers = GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => x.DocNo != null)
            .AsQueryable();

        if (sfxDealers.Any())
            query = query.Where(x => x.DealerCode != null &&
                                     sfxDealers.Contains(x.DealerCode));

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
                InvoiceNo   = x.DocNo,
                InvoiceDate = x.DocDate,
                InvoiceType = x.DocType,
                JobNo       = x.JobNo,
                JobDate     = x.JobDate,
                x.ChassisNo,
                x.RegNo,
                x.Model,
                x.DealerCode,
                x.DealerName,
                x.PartyName,
                x.PartyMobile,
                x.JobCardType,
                x.TotalAmount,
                x.Location
            })
            .Distinct()
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/dealers
    // ─────────────────────────────────────────────────────
    [HttpGet("dealers")]
    public async Task<IActionResult> GetConfiguredDealers()
    {
        var codes = GetShadowfaxDealerCodes();

        return Ok(new
        {
            ConfiguredDealerCodes = codes,
            Count = codes.Count,
            Note = codes.Any()
                ? "Dealer codes fetched successfully."
                : "No dealer codes found."
        });
    }
}