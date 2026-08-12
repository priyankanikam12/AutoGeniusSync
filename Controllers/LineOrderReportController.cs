using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LineOrderReportController : ControllerBase
{
    private readonly AppDbContext _db;
    public LineOrderReportController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? dealerCode, [FromQuery] string? chassisNo,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsLineOrderReports.AsQueryable();
        if (!string.IsNullOrEmpty(dealerCode)) q = q.Where(x => x.DealerCode == dealerCode);
        if (!string.IsNullOrEmpty(chassisNo)) q = q.Where(x => x.ChassisNo != null && x.ChassisNo.Contains(chassisNo));
        if (!string.IsNullOrEmpty(from) && DateOnly.TryParse(from, out var f)) q = q.Where(x => x.JobDate >= f);
        if (!string.IsNullOrEmpty(to) && DateOnly.TryParse(to, out var t)) q = q.Where(x => x.JobDate <= t);

        var total = await q.CountAsync();
        var records = await q.OrderByDescending(x => x.JobDate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<LorRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.Lor(r.DealerCode, r.UniqueId, r.DocNo, r.ItemName);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existingKeys = (await _db.DmsLineOrderReports
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsLineOrderReport>();
        var skipped = new List<string>();

        foreach (var r in deduped)
        {
            if (existingKeys.Contains(r.UniqueKey!)) { skipped.Add(r.UniqueKey!); continue; }
            toInsert.Add(MapLor(r));
        }

        if (toInsert.Count > 0) { await _db.DmsLineOrderReports.AddRangeAsync(toInsert); await _db.SaveChangesAsync(); }
        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<LorRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.Lor(r.DealerCode, r.UniqueId, r.DocNo, r.ItemName);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existing = await _db.DmsLineOrderReports.Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey)).ToListAsync();
        var lookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;
        foreach (var r in deduped)
        {
            if (lookup.TryGetValue(r.UniqueKey!, out var row)) { ApplyLor(row, r); row.UpdatedAt = DateTime.UtcNow; updated++; }
            else { _db.DmsLineOrderReports.Add(MapLor(r)); inserted++; }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    private static DmsLineOrderReport MapLor(LorRecordDto r) => new()
    {
        DealerName = r.DealerName, DealerCode = r.DealerCode, UniqueId = r.UniqueId, LocCode = r.LocCode,
        DocDate = r.DocDate, DocNo = r.DocNo, DocType = r.DocType, JobDate = r.JobDate, JobNo = r.JobNo,
        BrandName = r.BrandName, Model = r.Model, JobCardType = r.JobCardType, PaymentMode = r.PaymentMode,
        PartyName = r.PartyName, PartyMobile = r.PartyMobile, RegNo = r.RegNo, VehicleType = r.VehicleType,
        ChassisNo = r.ChassisNo, Location = r.Location, ItemName = r.ItemName, ItemDescription = r.ItemDescription,
        ItemType = r.ItemType, Qty = r.Qty, Rate = r.Rate, Total = r.Total, SgstPer = r.SgstPer,
        SgstAmount = r.SgstAmount, CgstPer = r.CgstPer, CgstAmount = r.CgstAmount, IgstPer = r.IgstPer,
        IgstAmount = r.IgstAmount, Discount = r.Discount, TotalAmount = r.TotalAmount, Mrp = r.Mrp,
        DealerType = r.DealerType, RowHash = r.RowHash, UniqueKey = r.UniqueKey,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void ApplyLor(DmsLineOrderReport row, LorRecordDto r)
    {
        row.DealerName = r.DealerName; row.DealerCode = r.DealerCode; row.UniqueId = r.UniqueId; row.LocCode = r.LocCode;
        row.DocDate = r.DocDate; row.DocNo = r.DocNo; row.DocType = r.DocType; row.JobDate = r.JobDate; row.JobNo = r.JobNo;
        row.BrandName = r.BrandName; row.Model = r.Model; row.JobCardType = r.JobCardType; row.PaymentMode = r.PaymentMode;
        row.PartyName = r.PartyName; row.PartyMobile = r.PartyMobile; row.RegNo = r.RegNo; row.VehicleType = r.VehicleType;
        row.ChassisNo = r.ChassisNo; row.Location = r.Location; row.ItemName = r.ItemName; row.ItemDescription = r.ItemDescription;
        row.ItemType = r.ItemType; row.Qty = r.Qty; row.Rate = r.Rate; row.Total = r.Total; row.SgstPer = r.SgstPer;
        row.SgstAmount = r.SgstAmount; row.CgstPer = r.CgstPer; row.CgstAmount = r.CgstAmount; row.IgstPer = r.IgstPer;
        row.IgstAmount = r.IgstAmount; row.Discount = r.Discount; row.TotalAmount = r.TotalAmount; row.Mrp = r.Mrp;
        row.DealerType = r.DealerType; row.RowHash = r.RowHash;
    }
}