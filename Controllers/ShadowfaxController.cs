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

    public ShadowfaxController(AppDbContext db, IConfiguration config, ILogger<ShadowfaxController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ── FIX: no longer gating on DMS_ShadowfaxChassisMaster (the locally
    // uploaded 3,285-chassis CSV). That list is a secondary, manually
    // maintained snapshot — it can drift out of sync with what the ERP
    // itself reports (new bikes added, CSV not re-uploaded, chassis
    // typos, etc.), and gating on it was silently hiding legitimate
    // Shadowfax data or letting in rows that happened to share a chassis
    // number with the CSV.
    //
    // Source of truth is now exactly what /V1/erpreport/lor (via
    // ErpApiService -> DataSyncService -> DMS_LineOrderReport) reports
    // as Shadowfax, using the same two ERP-native signals everywhere:
    //   1) DealerCode is one of the configured Shadowfax dealer codes
    //      (ShadowfaxSettings:DealerCodes in appsettings.json), AND
    //   2) PartyName starts with "shadowfax" (case-insensitive)
    // No chassis whitelist involved — chassisNo (when supplied) is now
    // purely an optional narrowing/search filter, not a gate.
    // ──────────────────────────────────────────────────────────────
    private List<string> GetShadowfaxDealerCodes()
    {
        var codes = _config.GetSection("ShadowfaxSettings:DealerCodes").Get<List<string>>() ?? new();

        if (!codes.Any())
        {
            _logger.LogWarning(
                "ShadowfaxSettings:DealerCodes is empty in appsettings.json — " +
                "every /api/shadowfax/* endpoint will return zero records.");
        }

        return codes;
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/service-history
    // Filter: DealerCode in configured Shadowfax dealer list
    //         AND PartyName starts with "shadowfax" (case-insensitive)
    // ─────────────────────────────────────────────────────
    [HttpGet("service-history")]
    public async Task<IActionResult> GetServiceHistory(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? jobNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();

        var query = _db.DmsServiceHistories
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                    && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

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
                x.DealerCode,
                x.JobNo,
                x.JobDate,
                x.ChassisNo,
                x.RegNo,
                x.Model,
                x.BrandName,
                x.PartyName,
                x.MobileNumber,
                x.DocNo,
                x.DocType,
                x.DocDate,
                x.InvoiceDate,
                x.JobType,
                x.NetTotal,
                x.EstimatedJobExpenses,
                x.Parts,
                x.Labour
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/vehicle-sales
    // Filter: DealerCode in configured Shadowfax dealer list
    //         AND SoldTo starts with "shadowfax" (case-insensitive)
    // NOTE: DmsVehicleSale has no confirmed "PartyName" field — SoldTo
    // is used as the closest stand-in for the customer/party name check.
    // Confirm this is the correct field before relying on this in prod.
    // ─────────────────────────────────────────────────────
    [HttpGet("vehicle-sales")]
    public async Task<IActionResult> GetVehicleSales(
        [FromQuery] string? chassisNo = null,
        [FromQuery] string? invoiceNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();

        var query = _db.DmsVehicleSales
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                    && x.SoldTo != null && EF.Functions.Like(x.SoldTo, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(invoiceNo))
            query = query.Where(x => x.InvoiceNo == invoiceNo);

        if (from.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(from.Value);
            query = query.Where(x => x.InvoiceDate >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = DateOnly.FromDateTime(to.Value);
            query = query.Where(x => x.InvoiceDate <= toDate);
        }

        var total = await query.CountAsync();

        var records = await query
            .OrderByDescending(x => x.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.DealerCode,
                x.DealerName,
                x.InvoiceNo,
                x.InvoiceDate,
                x.ChassisNo,
                x.ItemModel,
                x.VehicleType,
                x.SoldTo,
                x.CusMob,
                x.PartyEmail,
                x.City,
                x.State,
                x.NetAmount,
                x.ItemRate,
                x.FinancedBy,
                x.FinAmount
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/vehicles
    // One row per job, aggregated across all its line items.
    // ─────────────────────────────────────────────────────
    [HttpGet("vehicles")]
    public async Task<IActionResult> GetVehicles(
        [FromQuery] string? chassisNo = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

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

        var grouped = query
            .GroupBy(x => new { x.DealerCode, x.JobNo, x.ChassisNo })
            .Select(g => new
            {
                g.Key.ChassisNo,
                g.Key.JobNo,
                g.Key.DealerCode,
                RegNo        = g.Select(x => x.RegNo).FirstOrDefault(),
                Model        = g.Select(x => x.Model).FirstOrDefault(),
                DealerName   = g.Select(x => x.DealerName).FirstOrDefault(),
                PartyName    = g.Select(x => x.PartyName).FirstOrDefault(),
                MobileNumber = g.Select(x => x.PartyMobile).FirstOrDefault(),
                DocNo        = g.Select(x => x.DocNo).FirstOrDefault(),
                DocType      = g.Select(x => x.DocType).FirstOrDefault(),
                Location     = g.Select(x => x.Location).FirstOrDefault(),
                PaymentMode  = g.Select(x => x.PaymentMode).FirstOrDefault(),
                JobDate      = g.Max(x => x.JobDate),
                DocDate      = g.Max(x => x.DocDate),
                JobCardType  = g.Select(x => x.JobCardType).FirstOrDefault(),
                NetTotal     = g.Sum(x => x.TotalAmount ?? 0)
            });

        var total = await grouped.CountAsync();

        var records = await grouped
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ShadowfaxVehicleDto
            {
                ChassisNo           = x.ChassisNo,
                JobNo               = x.JobNo,
                RegNo               = x.RegNo,
                Model               = x.Model,
                DealerCode          = x.DealerCode,
                DealerName          = x.DealerName,
                PartyName           = x.PartyName,
                MobileNumber        = x.MobileNumber,
                DocNo               = x.DocNo,
                DocType             = x.DocType,
                Location            = x.Location,
                PaymentMode         = x.PaymentMode,
                JobcardCreationDate = x.JobDate,
                CompletionDate      = x.DocDate,
                RepairType          = x.JobCardType,
                NetTotal            = x.NetTotal,
                Status = x.DocDate != null ? "Repair Complete"
                       : x.JobDate != null ? "In Repair"
                       : "Not in Hub"
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/parts — line-item level detail
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
        var dealerCodes = GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => (x.ItemType == "Parts" || x.ItemType == "Part")
                     && x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

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
                x.PartyMobile,
                x.JobDate,
                InvoiceNo   = x.DocNo,
                InvoiceDate = x.DocDate,
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
    // GET /api/shadowfax/invoice-jobcard — one row per invoice,
    // aggregated across all line items.
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
        var dealerCodes = GetShadowfaxDealerCodes();

        var query = _db.DmsLineOrderReports
            .Where(x => x.DocNo != null
                     && x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

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

        var grouped = query
            .GroupBy(x => new { x.DealerCode, x.DocNo, x.ChassisNo })
            .Select(g => new
            {
                InvoiceNo    = g.Key.DocNo,
                InvoiceDate  = g.Max(x => x.DocDate),
                InvoiceType  = g.Select(x => x.DocType).FirstOrDefault(),
                JobNo        = g.Select(x => x.JobNo).FirstOrDefault(),
                JobDate      = g.Max(x => x.JobDate),
                ChassisNo    = g.Key.ChassisNo,
                RegNo        = g.Select(x => x.RegNo).FirstOrDefault(),
                Model        = g.Select(x => x.Model).FirstOrDefault(),
                DealerCode   = g.Key.DealerCode,
                DealerName   = g.Select(x => x.DealerName).FirstOrDefault(),
                PartyName    = g.Select(x => x.PartyName).FirstOrDefault(),
                PartyMobile  = g.Select(x => x.PartyMobile).FirstOrDefault(),
                JobCardType  = g.Select(x => x.JobCardType).FirstOrDefault(),
                Location     = g.Select(x => x.Location).FirstOrDefault(),
                TotalAmount  = g.Sum(x => x.TotalAmount ?? 0)
            });

        var total = await grouped.CountAsync();

        var records = await grouped
            .OrderByDescending(x => x.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/chassis-master-status
    // Kept for visibility into the uploaded CSV, but it no longer
    // gates any /api/shadowfax/* query — informational only now.
    // ─────────────────────────────────────────────────────
    [HttpGet("chassis-master-status")]
    public async Task<IActionResult> ChassisMasterStatus()
    {
        var count = await _db.DmsShadowfaxChassisMasters.CountAsync();
        return Ok(new
        {
            TotalChassisLoaded = count,
            Note = "This list is informational only — /api/shadowfax/* endpoints " +
                   "filter by DealerCode + PartyName (ERP-native fields), not this table."
        });
    }
}