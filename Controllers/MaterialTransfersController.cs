using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MaterialTransfersController : ControllerBase
{
    private readonly AppDbContext _db;

    public MaterialTransfersController(AppDbContext db)
    {
        _db = db;
    }

    // ── Server-maintained audit trail (the "Action" column) ──
    // Appends one {action, at, payload} entry to whatever JSON array is already
    // stored in Action, and returns the updated array as a string. Never
    // overwrites history: the first insert starts the array, every later
    // update grows it. Clients never send or see this field on input.
    private static string AppendActionLog(string? existingLog, string actionType, object? payload)
    {
        System.Text.Json.Nodes.JsonArray array;

        if (!string.IsNullOrWhiteSpace(existingLog))
        {
            try
            {
                array = System.Text.Json.Nodes.JsonNode.Parse(existingLog) as System.Text.Json.Nodes.JsonArray
                        ?? new System.Text.Json.Nodes.JsonArray();
            }
            catch
            {
                array = new System.Text.Json.Nodes.JsonArray();
            }
        }
        else
        {
            array = new System.Text.Json.Nodes.JsonArray();
        }

        var entry = new System.Text.Json.Nodes.JsonObject
        {
            ["action"]  = actionType,
            ["at"]      = DateTime.UtcNow.ToString("O"),
            ["payload"] = payload == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(payload)
        };

        array.Add(entry);
        return array.ToJsonString();
    }

    // GET /api/materialtransfers?dealerCode=CUS9999&from=2026-08-01&to=2026-08-31&page=1&pageSize=100
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? dealerCode,
        [FromQuery] string? docType,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsMaterialTransfers.AsQueryable();

        if (!string.IsNullOrEmpty(dealerCode))
            q = q.Where(x => x.DealerCode == dealerCode);

        if (!string.IsNullOrEmpty(docType))
            q = q.Where(x => x.DocType == docType);

        if (!string.IsNullOrEmpty(from) && DateTime.TryParse(from, out var f))
            q = q.Where(x => x.DocDate >= f);

        if (!string.IsNullOrEmpty(to) && DateTime.TryParse(to, out var t))
            q = q.Where(x => x.DocDate <= t);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(x => x.DocDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // GET /api/materialtransfers/all — everything, no filters, no paging
    [HttpGet("all")]
    public async Task<IActionResult> GetAll()
    {
        var records = await _db.DmsMaterialTransfers
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)
            .OrderByDescending(x => x.DocDate)
            .ToListAsync();

        return Ok(records);
    }

    // GET /api/materialtransfers/5
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var record = await _db.DmsMaterialTransfers
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)
            .FirstOrDefaultAsync(x => x.Id == id);

        return record == null ? NotFound() : Ok(record);
    }

    // GET /api/materialtransfers/dealer/CUS9999
    [HttpGet("dealer/{dealerCode}")]
    public async Task<IActionResult> GetByDealer(string dealerCode,
        [FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsMaterialTransfers.Where(x => x.DealerCode == dealerCode);

        if (DateTime.TryParse(from, out var f)) q = q.Where(x => x.DocDate >= f);
        if (DateTime.TryParse(to, out var t))   q = q.Where(x => x.DocDate <= t);

        var records = await q
            .OrderByDescending(x => x.DocDate)
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)
            .ToListAsync();

        return Ok(records);
    }

    // GET /api/materialtransfers/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary([FromQuery] string? from, [FromQuery] string? to)
    {
        var q = _db.DmsMaterialTransfers.Include(x => x.Items).AsQueryable();

        if (DateTime.TryParse(from, out var f)) q = q.Where(x => x.DocDate >= f);
        if (DateTime.TryParse(to, out var t))   q = q.Where(x => x.DocDate <= t);

        var summary = await q
            .GroupBy(x => x.DealerCode)
            .Select(g => new
            {
                DealerCode      = g.Key,
                DealerName      = g.Select(x => x.DealerName).FirstOrDefault(),
                TotalDocuments  = g.Count(),
                TotalItemLines  = g.SelectMany(x => x.Items).Count(),
                TotalItemValue  = g.SelectMany(x => x.Items).Sum(i => i.Qty * i.Rate)
            })
            .OrderByDescending(x => x.TotalDocuments)
            .ToListAsync();

        return Ok(summary);
    }

    // POST /api/materialtransfers/bulk — insert-only, skip documents whose UniqueKey already exists
    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<MaterialTransferRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        var mapped = records.Select(r => (Dto: r, Entity: MapMaterialTransfer(r))).ToList();
        var deduped = mapped.GroupBy(m => m.Entity.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(m => m.Entity.UniqueKey!).ToList();

        var existingKeys = (await _db.DmsMaterialTransfers
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsMaterialTransfer>();
        var skipped = new List<string>();

        foreach (var m in deduped)
        {
            if (existingKeys.Contains(m.Entity.UniqueKey!)) { skipped.Add(m.Entity.UniqueKey!); continue; }

            m.Entity.Action = AppendActionLog(null, "BULK_INSERT", m.Dto);
            toInsert.Add(m.Entity);
        }

        if (toInsert.Count > 0) { await _db.DmsMaterialTransfers.AddRangeAsync(toInsert); await _db.SaveChangesAsync(); }
        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    // PUT /api/materialtransfers/bulk — insert new, and for existing documents replace
    // the header fields + entirely replace the Items/LabourLines collections (a material
    // transfer's line items are the source of truth from the sender each time, not
    // something we merge field-by-field).
    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<MaterialTransferRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        var mapped = records.Select(r => (Dto: r, Entity: MapMaterialTransfer(r))).ToList();
        var deduped = mapped.GroupBy(m => m.Entity.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(m => m.Entity.UniqueKey!).ToList();

        var existing = await _db.DmsMaterialTransfers
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)
            .ToListAsync();
        var existingLookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;

        foreach (var m in deduped)
        {
            if (existingLookup.TryGetValue(m.Entity.UniqueKey!, out var current))
            {
                current.DealerName      = m.Entity.DealerName;
                current.DealerCode      = m.Entity.DealerCode;
                current.SourceUniqueId  = m.Entity.SourceUniqueId;
                current.SourceJobId     = m.Entity.SourceJobId;
                current.DocNo           = m.Entity.DocNo;
                current.DocDate         = m.Entity.DocDate;
                current.DocType         = m.Entity.DocType;
                current.Location        = m.Entity.Location;
                current.LocCode         = m.Entity.LocCode;
                current.TechnicianName  = m.Entity.TechnicianName;
                current.UpdatedAt       = DateTime.UtcNow;
                current.Action          = AppendActionLog(current.Action, "BULK_UPDATE", m.Dto);

                // replace children wholesale — cascade delete on the FK removes labor
                // lines automatically when their parent item is removed here.
                _db.DmsMaterialTransferItems.RemoveRange(current.Items);
                current.Items = m.Entity.Items;

                updated++;
            }
            else
            {
                m.Entity.Action = AppendActionLog(null, "BULK_INSERT", m.Dto);
                _db.DmsMaterialTransfers.Add(m.Entity);
                inserted++;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    // DELETE /api/materialtransfers/5
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var record = await _db.DmsMaterialTransfers.FirstOrDefaultAsync(x => x.Id == id);
        if (record == null) return NotFound();

        _db.DmsMaterialTransfers.Remove(record); // cascades to Items → LabourLines
        await _db.SaveChangesAsync();
        return Ok(new { Deleted = id });
    }

    // DELETE /api/materialtransfers/bulk  { "ids": [1,2,3] }  or  { "uniqueKeys": ["..."] }
    [HttpDelete("bulk")]
    public async Task<IActionResult> BulkDelete([FromBody] MaterialTransferDeleteRequest req)
    {
        var q = _db.DmsMaterialTransfers.AsQueryable();

        if (req.Ids?.Any() == true)
            q = q.Where(x => req.Ids.Contains(x.Id));
        else if (req.UniqueKeys?.Any() == true)
            q = q.Where(x => x.UniqueKey != null && req.UniqueKeys.Contains(x.UniqueKey));
        else
            return BadRequest(new { error = "Provide either 'ids' or 'uniqueKeys'." });

        var toDelete = await q.ToListAsync();
        _db.DmsMaterialTransfers.RemoveRange(toDelete);
        await _db.SaveChangesAsync();

        return Ok(new { Deleted = toDelete.Count });
    }

    // ─────────────────────────────────────────────────────────
    private static DmsMaterialTransfer MapMaterialTransfer(MaterialTransferRecordDto r)
    {
        var entity = new DmsMaterialTransfer
        {
            DealerName     = r.DealerName,
            DealerCode     = r.DealerCode,
            SourceUniqueId = r.UniqueId,
            SourceJobId    = r.JobId,
            DocNo          = r.DocNo,
            DocDate        = r.DocDate,
            DocType        = r.DocType,
            Location       = r.Location,
            LocCode        = r.LocCode,
            TechnicianName = r.TechnicianName,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow
        };
        entity.UniqueKey = UniqueKeyBuilder.MaterialTransfer(r.UniqueId, r.JobId);

        entity.Items = r.TranDetl.Select(i => new DmsMaterialTransferItem
        {
            SourceLineId     = i.Id,
            ItemIdno         = i.ItemIdno,
            ItemName         = i.ItemName,
            ItemDescription  = i.ItemDescription,
            ItemType         = i.ItemType,
            Qty              = i.Qty ?? 0,
            Rate             = i.Rate ?? 0,
            SgstPer          = i.SgstPer ?? 0,
            SgstAmount       = i.SgstAmount ?? 0,
            CgstPer          = i.CgstPer ?? 0,
            CgstAmount       = i.CgstAmount ?? 0,
            IgstPer          = i.IgstPer ?? 0,
            IgstAmount       = i.IgstAmount ?? 0,
            Discount         = i.Discount ?? 0,
            Mrp              = i.Mrp ?? 0,
            LabourLines      = i.LbrDetl.Select(l => new DmsMaterialTransferLabor
            {
                LbrIdno         = l.LbrIdno,
                LbrName         = l.LbrName,
                LbrDescription  = l.LbrDescription,
                LbrRate         = l.LbrRate ?? 0,
                SgstPer         = l.SgstPer ?? 0,
                SgstAmount      = l.SgstAmount ?? 0,
                CgstPer         = l.CgstPer ?? 0,
                CgstAmount      = l.CgstAmount ?? 0,
                IgstPer         = l.IgstPer ?? 0,
                IgstAmount      = l.IgstAmount ?? 0
            }).ToList()
        }).ToList();

        return entity;
    }
}