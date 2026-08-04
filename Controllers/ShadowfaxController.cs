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

    private List<string> GetShadowfaxDealerCodes()
    {
        var codes = _config.GetSection("ShadowfaxSettings:DealerCodes").Get<List<string>>() ?? new();
        if (!codes.Any())
            _logger.LogWarning("ShadowfaxSettings:DealerCodes is empty — every /api/shadowfax/* endpoint will return zero records.");
        return codes;
    }

    // service-history, vehicle-sales, parts, chassis-master-status — unchanged, no GroupBy so unaffected
    [HttpGet("service-history")]
    public async Task<IActionResult> GetServiceHistory(
        [FromQuery] string? chassisNo = null, [FromQuery] string? jobNo = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();
        var query = _db.DmsServiceHistories
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                    && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo)) query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));
        if (!string.IsNullOrEmpty(jobNo)) query = query.Where(x => x.JobNo == jobNo);
        if (from.HasValue) { var fromDate = DateOnly.FromDateTime(from.Value); query = query.Where(x => x.JobDate >= fromDate); }
        if (to.HasValue) { var toDate = DateOnly.FromDateTime(to.Value); query = query.Where(x => x.JobDate <= toDate); }

        var total = await query.CountAsync();
        var records = await query
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.DealerCode, x.JobNo, x.JobDate, x.ChassisNo, x.RegNo, x.Model, x.BrandName,
                x.PartyName, x.MobileNumber, x.DocNo, x.DocType, x.DocDate, x.InvoiceDate,
                x.JobType, x.NetTotal, x.EstimatedJobExpenses, x.Parts, x.Labour
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    [HttpGet("vehicle-sales")]
    public async Task<IActionResult> GetVehicleSales(
        [FromQuery] string? chassisNo = null, [FromQuery] string? invoiceNo = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();
        var query = _db.DmsVehicleSales
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                    && x.SoldTo != null && EF.Functions.Like(x.SoldTo, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo)) query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));
        if (!string.IsNullOrEmpty(invoiceNo)) query = query.Where(x => x.InvoiceNo == invoiceNo);
        if (from.HasValue) { var fromDate = DateOnly.FromDateTime(from.Value); query = query.Where(x => x.InvoiceDate >= fromDate); }
        if (to.HasValue) { var toDate = DateOnly.FromDateTime(to.Value); query = query.Where(x => x.InvoiceDate <= toDate); }

        var total = await query.CountAsync();
        var records = await query
            .OrderByDescending(x => x.InvoiceDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.DealerCode, x.DealerName, x.InvoiceNo, x.InvoiceDate, x.ChassisNo, x.ItemModel,
                x.VehicleType, x.SoldTo, x.CusMob, x.PartyEmail, x.City, x.State, x.NetAmount,
                x.ItemRate, x.FinancedBy, x.FinAmount
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/vehicles
    //
    // FIX (root cause of the June'26 timeout/500 crash): the previous
    // version used a single GroupBy with 8 separate
    // g.Select(x => x.Col).FirstOrDefault() calls. EF Core translates
    // each into its own correlated subquery PER GROUP. With Skip/Take
    // pagination, SQL Server must evaluate these subqueries for every
    // group up to the requested offset before returning a page — and
    // June'26 has jobs with many line items per chassis (confirmed:
    // some chassis numbers have 10+ duplicate rows), so the cumulative
    // subquery cost crosses the timeout threshold once the offset
    // lands in the ~700-900 range. This is NOT corrupted data — the
    // diagnostic queries for control characters and oversized numeric
    // values both returned zero rows.
    //
    // Fix: split into two cheap steps. Step 1 computes only the
    // aggregates needed for paging (Max/Sum) — a single efficient
    // GROUP BY. Step 2 fetches descriptive columns for ONLY the
    // current page's keys, picking one representative row per group
    // via a plain grouped Select instead of 8 correlated subqueries.
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

        var baseQuery = _db.DmsLineOrderReports
            .Where(x => x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            baseQuery = baseQuery.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

        if (from.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(from.Value);
            baseQuery = baseQuery.Where(x => x.JobDate >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = DateOnly.FromDateTime(to.Value);
            baseQuery = baseQuery.Where(x => x.JobDate <= toDate);
        }

        // Step 1: cheap aggregate-only GROUP BY for paging + totals.
        var totals = baseQuery
            .GroupBy(x => new { x.DealerCode, x.JobNo, x.ChassisNo })
            .Select(g => new
            {
                g.Key.DealerCode,
                g.Key.JobNo,
                g.Key.ChassisNo,
                JobDate  = g.Max(x => x.JobDate),
                DocDate  = g.Max(x => x.DocDate),
                NetTotal = g.Sum(x => x.TotalAmount ?? 0)
            });

        var total = await totals.CountAsync();

        var pagedKeys = await totals
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        if (!pagedKeys.Any())
            return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = Array.Empty<object>() });

        // Step 2: descriptive columns for only this page's job numbers —
        // one representative row per group, no per-column subqueries.
        var jobNos = pagedKeys.Select(k => k.JobNo).ToList();

        var descriptiveRows = await baseQuery
            .Where(x => jobNos.Contains(x.JobNo))
            .GroupBy(x => new { x.DealerCode, x.JobNo, x.ChassisNo })
            .Select(g => g.OrderBy(x => x.Id).First())
            .ToListAsync();

        var descLookup = descriptiveRows.ToDictionary(x => (x.DealerCode, x.JobNo, x.ChassisNo));

        var records = pagedKeys.Select(k =>
        {
            descLookup.TryGetValue((k.DealerCode, k.JobNo, k.ChassisNo), out var d);
            return new ShadowfaxVehicleDto
            {
                ChassisNo           = k.ChassisNo,
                JobNo               = k.JobNo,
                RegNo               = d?.RegNo,
                Model               = d?.Model,
                DealerCode          = k.DealerCode,
                DealerName          = d?.DealerName,
                PartyName           = d?.PartyName,
                MobileNumber        = d?.PartyMobile,
                DocNo               = d?.DocNo,
                DocType             = d?.DocType,
                Location            = d?.Location,
                PaymentMode         = d?.PaymentMode,
                JobcardCreationDate = k.JobDate,
                CompletionDate      = k.DocDate,
                RepairType          = d?.JobCardType,
                NetTotal            = k.NetTotal,
                Status = k.DocDate != null ? "Repair Complete"
                       : k.JobDate != null ? "In Repair"
                       : "Not in Hub"
            };
        });

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    [HttpGet("parts")]
    public async Task<IActionResult> GetParts(
        [FromQuery] string? chassisNo = null, [FromQuery] string? jobNo = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var dealerCodes = GetShadowfaxDealerCodes();
        var query = _db.DmsLineOrderReports
            .Where(x => (x.ItemType == "Parts" || x.ItemType == "Part")
                     && x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo)) query = query.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));
        if (!string.IsNullOrEmpty(jobNo)) query = query.Where(x => x.JobNo == jobNo);
        if (from.HasValue) { var fromDate = DateOnly.FromDateTime(from.Value); query = query.Where(x => x.JobDate >= fromDate); }
        if (to.HasValue) { var toDate = DateOnly.FromDateTime(to.Value); query = query.Where(x => x.JobDate <= toDate); }

        var total = await query.CountAsync();
        var records = await query
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.ChassisNo, x.JobNo, x.RegNo, x.Model, x.DealerCode, x.DealerName,
                x.PartyName, x.PartyMobile, x.JobDate,
                InvoiceNo = x.DocNo, InvoiceDate = x.DocDate,
                x.ItemName, x.ItemDescription, x.ItemType, x.Qty, x.Rate, x.Total,
                x.TotalAmount, x.SgstAmount, x.CgstAmount, x.IgstAmount, x.Discount
            })
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // ─────────────────────────────────────────────────────
    // GET /api/shadowfax/invoice-jobcard — same fix as GetVehicles above
    // (identical 9-subquery-per-group pattern was present here too).
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

        var baseQuery = _db.DmsLineOrderReports
            .Where(x => x.DocNo != null
                     && x.DealerCode != null && dealerCodes.Contains(x.DealerCode)
                     && x.PartyName != null && EF.Functions.Like(x.PartyName, "shadowfax%"))
            .AsQueryable();

        if (!string.IsNullOrEmpty(chassisNo))
            baseQuery = baseQuery.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));

        if (!string.IsNullOrEmpty(invoiceNo))
            baseQuery = baseQuery.Where(x => x.DocNo == invoiceNo);

        if (from.HasValue)
        {
            var fromDate = DateOnly.FromDateTime(from.Value);
            baseQuery = baseQuery.Where(x => x.DocDate >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = DateOnly.FromDateTime(to.Value);
            baseQuery = baseQuery.Where(x => x.DocDate <= toDate);
        }

        var totals = baseQuery
            .GroupBy(x => new { x.DealerCode, x.DocNo, x.ChassisNo })
            .Select(g => new
            {
                g.Key.DealerCode,
                g.Key.DocNo,
                g.Key.ChassisNo,
                InvoiceDate = g.Max(x => x.DocDate),
                JobDate     = g.Max(x => x.JobDate),
                TotalAmount = g.Sum(x => x.TotalAmount ?? 0)
            });

        var total = await totals.CountAsync();

        var pagedKeys = await totals
            .OrderByDescending(x => x.InvoiceDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        if (!pagedKeys.Any())
            return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = Array.Empty<object>() });

        var docNos = pagedKeys.Select(k => k.DocNo).ToList();

        var descriptiveRows = await baseQuery
            .Where(x => docNos.Contains(x.DocNo))
            .GroupBy(x => new { x.DealerCode, x.DocNo, x.ChassisNo })
            .Select(g => g.OrderBy(x => x.Id).First())
            .ToListAsync();

        var descLookup = descriptiveRows.ToDictionary(x => (x.DealerCode, x.DocNo, x.ChassisNo));

        var records = pagedKeys.Select(k =>
        {
            descLookup.TryGetValue((k.DealerCode, k.DocNo, k.ChassisNo), out var d);
            return new
            {
                InvoiceNo    = k.DocNo,
                InvoiceDate  = k.InvoiceDate,
                InvoiceType  = d?.DocType,
                JobNo        = d?.JobNo,
                JobDate      = k.JobDate,
                ChassisNo    = k.ChassisNo,
                RegNo        = d?.RegNo,
                Model        = d?.Model,
                DealerCode   = k.DealerCode,
                DealerName   = d?.DealerName,
                PartyName    = d?.PartyName,
                PartyMobile  = d?.PartyMobile,
                JobCardType  = d?.JobCardType,
                Location     = d?.Location,
                TotalAmount  = k.TotalAmount
            };
        });

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    [HttpGet("chassis-master-status")]
    public async Task<IActionResult> ChassisMasterStatus()
    {
        var count = await _db.DmsShadowfaxChassisMasters.CountAsync();
        return Ok(new
        {
            TotalChassisLoaded = count,
            Note = "This list is informational only — /api/shadowfax/* endpoints filter by DealerCode + PartyName (ERP-native fields), not this table."
        });
    }
}