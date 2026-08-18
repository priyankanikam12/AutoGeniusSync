using AutoGeniusSync.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using AutoGeniusSync.Helpers;

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

    // ── GET /api/servicehistory/share?from=2022-07-01&to=2022-07-31 ──
    // Same filters/paging as Get() above, but projects to ServiceHistoryShareDto,
    // which deliberately excludes: BrandName, VehicleType, IndividualAhbattery1-6,
    // Accessory, Oil, IsRowTotal. Use this endpoint whenever the result is going
    // to an external party, so those fields never leave the system by accident.
    [HttpGet("share")]
    public async Task<IActionResult> GetForShare(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? dealerCode,
        [FromQuery] string? chassisNo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var query = _db.DmsServiceHistories
            .Where(s => !s.IsRowTotal && s.JobStatus == "Closed")
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
            .Select(s => new ServiceHistoryShareDto
            {
                Id = s.Id,
                DealerCode = s.DealerCode,
                JobNo = s.JobNo,
                JobDate = s.JobDate,
                CompName = s.CompName,
                Location = s.Location,
                InTime = s.InTime,
                CloseTime = s.CloseTime,
                JobCategory = s.JobCategory,
                Ffrpercentage = s.Ffrpercentage,
                DocNo = s.DocNo,
                DocType = s.DocType,
                DocDate = s.DocDate,
                Model = s.Model,
                RegNo = s.RegNo,
                EngineNo = s.EngineNo,
                ChassisNo = s.ChassisNo,
                Kms = s.Kms,
                BatterySerialNo1 = s.BatterySerialNo1,
                BatterySerialNo2 = s.BatterySerialNo2,
                BatterySerialNo3 = s.BatterySerialNo3,
                BatterySerialNo4 = s.BatterySerialNo4,
                BatterySerialNo5 = s.BatterySerialNo5,
                BatterySerialNo6 = s.BatterySerialNo6,
                PartyName = s.PartyName,
                MobileNumber = s.MobileNumber,
                Supervisor = s.Supervisor,
                Technician = s.Technician,
                ServiceHead = s.ServiceHead,
                JobType = s.JobType,
                SaleDate = s.SaleDate,
                CouponNo = s.CouponNo,
                ExpectedDeliveryDate = s.ExpectedDeliveryDate,
                ProformaDate = s.ProformaDate,
                InvoiceDate = s.InvoiceDate,
                EstimatedJobExpenses = s.EstimatedJobExpenses,
                LabourHours = s.LabourHours,
                Parts = s.Parts,
                Labour = s.Labour,
                OutsideWork = s.OutsideWork,
                TotalWotax = s.TotalWotax,
                Gstamount = s.Gstamount,
                Igstamount = s.Igstamount,
                NetTotal = s.NetTotal,
                RepairType = s.RepairType,
                CompletionDate = s.CompletionDate,
                JobStatus = s.JobStatus,
                RowHash = s.RowHash,
                UniqueKey = s.UniqueKey,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            })
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

    // ── POST /api/servicehistory/bulk — insert-only, skip existing UniqueKey ──
    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<ServiceHistoryRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.ServiceHistory(r.DealerCode, r.JobNo, r.JobDate, r.ChassisNo);

        var dedupedIncoming = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();

        var keys = dedupedIncoming.Select(r => r.UniqueKey!).ToList();
        var existingKeys = (await _db.DmsServiceHistories
            .Where(s => s.UniqueKey != null && keys.Contains(s.UniqueKey))
            .Select(s => s.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsServiceHistory>();
        var skipped = new List<string>();

        foreach (var r in dedupedIncoming)
        {
            if (existingKeys.Contains(r.UniqueKey!)) { skipped.Add(r.UniqueKey!); continue; }
            toInsert.Add(MapServiceHistory(r));
        }

        if (toInsert.Count > 0)
        {
            await _db.DmsServiceHistories.AddRangeAsync(toInsert);
            await _db.SaveChangesAsync();
        }

        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    // ── PUT /api/servicehistory/bulk — upsert by UniqueKey ──
    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<ServiceHistoryRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.ServiceHistory(r.DealerCode, r.JobNo, r.JobDate, r.ChassisNo);

        var dedupedIncoming = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = dedupedIncoming.Select(r => r.UniqueKey!).ToList();

        var existing = await _db.DmsServiceHistories
            .Where(s => s.UniqueKey != null && keys.Contains(s.UniqueKey))
            .ToListAsync();
        var existingLookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;

        foreach (var r in dedupedIncoming)
        {
            if (existingLookup.TryGetValue(r.UniqueKey!, out var row))
            {
                ApplyServiceHistory(row, r);
                row.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.DmsServiceHistories.Add(MapServiceHistory(r));
                inserted++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    // ── POST /api/servicehistory ─────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ServiceHistoryImportRequest request)
    {
        if (request?.Records is null || request.Records.Count == 0)
            return BadRequest("No records supplied.");

        var incomingKeys = request.Records
            .Where(r => !string.IsNullOrEmpty(r.UniqueKey))
            .Select(r => r.UniqueKey!)
            .Distinct()
            .ToList();

        var existingKeys = await _db.DmsServiceHistories
            .Where(s => s.UniqueKey != null && incomingKeys.Contains(s.UniqueKey))
            .Select(s => s.UniqueKey!)
            .ToListAsync();

        var existingSet = new HashSet<string>(existingKeys);

        var toInsert = new List<DmsServiceHistory>();
        var skipped = new List<string>();

        foreach (var r in request.Records)
        {
            if (!string.IsNullOrEmpty(r.UniqueKey) && existingSet.Contains(r.UniqueKey))
            {
                skipped.Add(r.UniqueKey);
                continue;
            }

            toInsert.Add(new DmsServiceHistory
            {
                DealerCode           = r.DealerCode,
                JobNo                = r.JobNo,
                JobDate              = r.JobDate,
                CompName             = r.CompName,
                Location             = r.Location,
                InTime               = r.InTime,
                CloseTime            = r.CloseTime,
                JobCategory          = r.JobCategory,
                Ffrpercentage        = r.Ffrpercentage,
                DocNo                = r.DocNo,
                DocType              = r.DocType,
                DocDate              = r.DocDate,
                Model                = r.Model,
                BrandName            = r.BrandName,
                RegNo                = r.RegNo,
                VehicleType          = r.VehicleType,
                EngineNo             = r.EngineNo,
                ChassisNo            = r.ChassisNo,
                Kms                  = r.Kms,
                BatterySerialNo1     = r.BatterySerialNo1,
                BatterySerialNo2     = r.BatterySerialNo2,
                BatterySerialNo3     = r.BatterySerialNo3,
                BatterySerialNo4     = r.BatterySerialNo4,
                BatterySerialNo5     = r.BatterySerialNo5,
                BatterySerialNo6     = r.BatterySerialNo6,
                IndividualAhbattery1 = r.IndividualAhbattery1,
                IndividualAhbattery2 = r.IndividualAhbattery2,
                IndividualAhbattery3 = r.IndividualAhbattery3,
                IndividualAhbattery4 = r.IndividualAhbattery4,
                IndividualAhbattery5 = r.IndividualAhbattery5,
                IndividualAhbattery6 = r.IndividualAhbattery6,
                PartyName            = r.PartyName,
                MobileNumber         = r.MobileNumber,
                Supervisor           = r.Supervisor,
                Technician           = r.Technician,
                ServiceHead          = r.ServiceHead,
                JobType              = r.JobType,
                SaleDate             = r.SaleDate,
                CouponNo             = r.CouponNo,
                ExpectedDeliveryDate = r.ExpectedDeliveryDate,
                ProformaDate         = r.ProformaDate,
                InvoiceDate          = r.InvoiceDate,
                EstimatedJobExpenses = r.EstimatedJobExpenses,
                LabourHours          = r.LabourHours,
                Parts                = r.Parts,
                Accessory            = r.Accessory,
                Oil                  = r.Oil,
                Labour               = r.Labour,
                OutsideWork          = r.OutsideWork,
                TotalWotax           = r.TotalWotax,
                Gstamount            = r.Gstamount,
                Igstamount           = r.Igstamount,
                NetTotal             = r.NetTotal,
                IsRowTotal           = r.IsRowTotal,
                RepairType           = r.RepairType,
                CompletionDate       = r.CompletionDate,
                // JobStatus is DB-computed — never assign it here
                RowHash              = r.RowHash,
                UniqueKey            = r.UniqueKey,
                CreatedAt            = DateTime.UtcNow,
                UpdatedAt            = DateTime.UtcNow
            });
        }

        if (toInsert.Count > 0)
        {
            await _db.DmsServiceHistories.AddRangeAsync(toInsert);
            await _db.SaveChangesAsync();
        }

        return Ok(new
        {
            Inserted = toInsert.Count,
            SkippedDuplicates = skipped.Count,
            SkippedKeys = skipped
        });
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

    private static DmsServiceHistory MapServiceHistory(ServiceHistoryRecordDto r) => new()
    {
        DealerCode = r.DealerCode, JobNo = r.JobNo, JobDate = r.JobDate, CompName = r.CompName,
        Location = r.Location, InTime = r.InTime, CloseTime = r.CloseTime, JobCategory = r.JobCategory,
        Ffrpercentage = r.Ffrpercentage, DocNo = r.DocNo, DocType = r.DocType, DocDate = r.DocDate,
        Model = r.Model, BrandName = r.BrandName, RegNo = r.RegNo, VehicleType = r.VehicleType,
        EngineNo = r.EngineNo, ChassisNo = r.ChassisNo, Kms = r.Kms,
        BatterySerialNo1 = r.BatterySerialNo1, BatterySerialNo2 = r.BatterySerialNo2,
        BatterySerialNo3 = r.BatterySerialNo3, BatterySerialNo4 = r.BatterySerialNo4,
        BatterySerialNo5 = r.BatterySerialNo5, BatterySerialNo6 = r.BatterySerialNo6,
        IndividualAhbattery1 = r.IndividualAhbattery1, IndividualAhbattery2 = r.IndividualAhbattery2,
        IndividualAhbattery3 = r.IndividualAhbattery3, IndividualAhbattery4 = r.IndividualAhbattery4,
        IndividualAhbattery5 = r.IndividualAhbattery5, IndividualAhbattery6 = r.IndividualAhbattery6,
        PartyName = r.PartyName, MobileNumber = r.MobileNumber, Supervisor = r.Supervisor,
        Technician = r.Technician, ServiceHead = r.ServiceHead, JobType = r.JobType, SaleDate = r.SaleDate,
        CouponNo = r.CouponNo, ExpectedDeliveryDate = r.ExpectedDeliveryDate, ProformaDate = r.ProformaDate,
        InvoiceDate = r.InvoiceDate, EstimatedJobExpenses = r.EstimatedJobExpenses, LabourHours = r.LabourHours,
        Parts = r.Parts, Accessory = r.Accessory, Oil = r.Oil, Labour = r.Labour, OutsideWork = r.OutsideWork,
        TotalWotax = r.TotalWotax, Gstamount = r.Gstamount, Igstamount = r.Igstamount, NetTotal = r.NetTotal,
        IsRowTotal = r.IsRowTotal, RepairType = r.RepairType, CompletionDate = r.CompletionDate,
        RowHash = r.RowHash, UniqueKey = r.UniqueKey, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void ApplyServiceHistory(DmsServiceHistory row, ServiceHistoryRecordDto r)
    {
        row.DealerCode = r.DealerCode; row.JobNo = r.JobNo; row.JobDate = r.JobDate; row.CompName = r.CompName;
        row.Location = r.Location; row.InTime = r.InTime; row.CloseTime = r.CloseTime; row.JobCategory = r.JobCategory;
        row.Ffrpercentage = r.Ffrpercentage; row.DocNo = r.DocNo; row.DocType = r.DocType; row.DocDate = r.DocDate;
        row.Model = r.Model; row.BrandName = r.BrandName; row.RegNo = r.RegNo; row.VehicleType = r.VehicleType;
        row.EngineNo = r.EngineNo; row.ChassisNo = r.ChassisNo; row.Kms = r.Kms;
        row.BatterySerialNo1 = r.BatterySerialNo1; row.BatterySerialNo2 = r.BatterySerialNo2;
        row.BatterySerialNo3 = r.BatterySerialNo3; row.BatterySerialNo4 = r.BatterySerialNo4;
        row.BatterySerialNo5 = r.BatterySerialNo5; row.BatterySerialNo6 = r.BatterySerialNo6;
        row.IndividualAhbattery1 = r.IndividualAhbattery1; row.IndividualAhbattery2 = r.IndividualAhbattery2;
        row.IndividualAhbattery3 = r.IndividualAhbattery3; row.IndividualAhbattery4 = r.IndividualAhbattery4;
        row.IndividualAhbattery5 = r.IndividualAhbattery5; row.IndividualAhbattery6 = r.IndividualAhbattery6;
        row.PartyName = r.PartyName; row.MobileNumber = r.MobileNumber; row.Supervisor = r.Supervisor;
        row.Technician = r.Technician; row.ServiceHead = r.ServiceHead; row.JobType = r.JobType; row.SaleDate = r.SaleDate;
        row.CouponNo = r.CouponNo; row.ExpectedDeliveryDate = r.ExpectedDeliveryDate; row.ProformaDate = r.ProformaDate;
        row.InvoiceDate = r.InvoiceDate; row.EstimatedJobExpenses = r.EstimatedJobExpenses; row.LabourHours = r.LabourHours;
        row.Parts = r.Parts; row.Accessory = r.Accessory; row.Oil = r.Oil; row.Labour = r.Labour; row.OutsideWork = r.OutsideWork;
        row.TotalWotax = r.TotalWotax; row.Gstamount = r.Gstamount; row.Igstamount = r.Igstamount; row.NetTotal = r.NetTotal;
        row.IsRowTotal = r.IsRowTotal; row.RepairType = r.RepairType; row.CompletionDate = r.CompletionDate;
        row.RowHash = r.RowHash;
        // UniqueKey deliberately not overwritten — it's the match key
    }
}
