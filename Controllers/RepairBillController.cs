using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AutoGeniusSync.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RepairBillController : ControllerBase
    {
        // NOTE: swap "AppDbContext" for whatever your actual DbContext class is named
        // (same one your existing controllers inject) if it differs.
        private readonly AppDbContext _context;
        private readonly IConfiguration _config;

        public RepairBillController(AppDbContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        // Gate for all write endpoints (POST/PUT/DELETE) — reads appsettings "ApiKeys:Cogzim".
        private bool IsValidApiKey(string? apiKey)
        {
            var expected = _config["ApiKeys:Cogzim"];
            return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
        }

        // GET api/repairbill?dealerCode=Cus9999&page=1&pageSize=50
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] string? dealerCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 50;

            var query = _context.RepairBills.Include(p => p.Items).AsQueryable();

            if (!string.IsNullOrWhiteSpace(dealerCode))
                query = query.Where(p => p.DealerCode == dealerCode);

            var total = await query.CountAsync();
            var items = await query
                .OrderByDescending(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { total, page, pageSize, records = items });
        }

        // GET api/repairbill/all
        [HttpGet("all")]
        public async Task<IActionResult> GetAll()
        {
            var items = await _context.RepairBills.Include(p => p.Items).ToListAsync();
            return Ok(items);
        }

        // GET api/repairbill/5
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var item = await _context.RepairBills.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
            if (item == null) return NotFound();
            return Ok(item);
        }

        // GET api/repairbill/dealer/Cus9999
        [HttpGet("dealer/{dealerCode}")]
        public async Task<IActionResult> GetByDealer(string dealerCode)
        {
            var items = await _context.RepairBills
                .Include(p => p.Items)
                .Where(p => p.DealerCode == dealerCode)
                .ToListAsync();
            return Ok(items);
        }

        // GET api/repairbill/chassis/P6DEC12NPBC000004
        [HttpGet("chassis/{chassisNo}")]
        public async Task<IActionResult> GetByChassis(string chassisNo)
        {
            var items = await _context.RepairBills
                .Include(p => p.Items)
                .Where(p => p.ChassisNo == chassisNo)
                .ToListAsync();
            return Ok(items);
        }

        // POST api/repairbill/bulk — insert-only, silently skips records whose (uniqueId, uniqueKey) already exist
        [HttpPost("bulk")]
        public async Task<IActionResult> BulkInsert([FromHeader(Name = "X-API-Key")] string? apiKey, [FromBody] List<RepairBillRecordDto> records)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (records == null || records.Count == 0)
                return BadRequest("No records supplied.");

            var mapped = records.Select(r => MapToEntity(r)).ToList();

            var keys = mapped.Select(e => e.ComputedUniqueKey).Distinct().ToList();
            var existingKeys = (await _context.RepairBills
                .Where(p => keys.Contains(p.ComputedUniqueKey))
                .Select(p => p.ComputedUniqueKey)
                .ToListAsync())
                .ToHashSet();

            var toInsert = new List<DmsRepairBill>();
            var skipped = new List<string>();

            foreach (var entity in mapped)
            {
                if (existingKeys.Contains(entity.ComputedUniqueKey))
                {
                    skipped.Add(entity.ComputedUniqueKey);
                    continue;
                }
                toInsert.Add(entity);
                existingKeys.Add(entity.ComputedUniqueKey); // guard against dupes within the same batch
            }

            var inserted = 0;
            try
            {
                await _context.RepairBills.AddRangeAsync(toInsert);
                await _context.SaveChangesAsync();
                inserted = toInsert.Count;
            }
            catch (DbUpdateException)
            {
                _context.ChangeTracker.Clear();
                inserted = 0;
                foreach (var entity in toInsert)
                {
                    try
                    {
                        _context.RepairBills.Add(entity);
                        await _context.SaveChangesAsync();
                        inserted++;
                    }
                    catch (DbUpdateException)
                    {
                        _context.ChangeTracker.Clear();
                        skipped.Add(entity.ComputedUniqueKey);
                    }
                }
            }

            return Ok(new { inserted, skipped = skipped.Count, skippedKeys = skipped });
        }

        // PUT api/repairbill/bulk — insert new (uniqueId, uniqueKey) combinations, update existing ones (including their line items)
        [HttpPut("bulk")]
        public async Task<IActionResult> BulkUpsert([FromHeader(Name = "X-API-Key")] string? apiKey, [FromBody] List<RepairBillRecordDto> records)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (records == null || records.Count == 0)
                return BadRequest("No records supplied.");

            var mapped = records.Select(r => MapToEntity(r)).ToList();
            var keys = mapped.Select(e => e.ComputedUniqueKey).Distinct().ToList();

            var existing = await _context.RepairBills
                .Include(p => p.Items)
                .Where(p => keys.Contains(p.ComputedUniqueKey))
                .ToDictionaryAsync(p => p.ComputedUniqueKey);

            var toInsert = new List<DmsRepairBill>();
            var updated = 0;

            foreach (var entity in mapped)
            {
                if (existing.TryGetValue(entity.ComputedUniqueKey, out var current))
                {
                    ApplyHeaderFields(current, entity);
                    _context.RepairBillItems.RemoveRange(current.Items);
                    current.Items = entity.Items;
                    current.UpdatedAt = DateTime.UtcNow;
                    updated++;
                }
                else
                {
                    toInsert.Add(entity);
                }
            }

            var inserted = 0;
            try
            {
                if (toInsert.Count > 0)
                    await _context.RepairBills.AddRangeAsync(toInsert);
                await _context.SaveChangesAsync();
                inserted = toInsert.Count;
            }
            catch (DbUpdateException)
            {
                _context.ChangeTracker.Clear();
                foreach (var entity in toInsert)
                {
                    try
                    {
                        _context.RepairBills.Add(entity);
                        await _context.SaveChangesAsync();
                        inserted++;
                    }
                    catch (DbUpdateException)
                    {
                        _context.ChangeTracker.Clear();
                    }
                }
            }

            return Ok(new { inserted, updated });
        }

        // DELETE api/repairbill/5
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id, [FromHeader(Name = "X-API-Key")] string? apiKey)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            var item = await _context.RepairBills.FindAsync(id);
            if (item == null) return NotFound();
            _context.RepairBills.Remove(item);
            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE api/repairbill/bulk  — body: [ { "uniqueId": 19, "uniqueKey": 21 }, ... ]
        [HttpDelete("bulk")]
        public async Task<IActionResult> BulkDelete([FromHeader(Name = "X-API-Key")] string? apiKey, [FromBody] List<RepairBillDeleteRequestDto> requests)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (requests == null || requests.Count == 0)
                return BadRequest("No records supplied.");

            var keys = requests.Select(r => UniqueKeyBuilder.RepairBill(r.UniqueId, r.UniqueKey)).ToList();

            var toDelete = await _context.RepairBills
                .Where(p => keys.Contains(p.ComputedUniqueKey))
                .ToListAsync();

            _context.RepairBills.RemoveRange(toDelete);
            await _context.SaveChangesAsync();

            return Ok(new { deleted = toDelete.Count });
        }

        private static DmsRepairBill MapToEntity(RepairBillRecordDto r)
        {
            return new DmsRepairBill
            {
                DealerName = r.DealerName,
                DealerCode = r.DealerCode,
                UniqueKey = r.UniqueKey,
                UniqueId = r.UniqueId,
                InvoiceNo = r.InvoiceNo,
                InvoiceDate = r.InvoiceDate,
                Location = r.Location,
                PartyName = r.PartyName,
                BillType = r.BillType,
                CashType = r.CashType,
                CashAccount = r.CashAccount,
                RegNo = r.RegNo,
                ChassisNo = r.ChassisNo,
                ComputedUniqueKey = UniqueKeyBuilder.RepairBill(r.UniqueId, r.UniqueKey),
                Items = (r.ProformaDetl ?? new List<RepairBillDetailDto>()).Select(d => new DmsRepairBillItem
                {
                    SourceItemId = d.Id,
                    ItemIdno = d.ItemIdno,
                    ItemCode = d.ItemCode,
                    ItemDesc = d.ItemDesc,
                    ItemType = d.ItemType,
                    Qty = d.Qty,
                    Rate = d.Rate,
                    IssueType = d.IssueType,
                    SgstPer = d.SgstPer,
                    SgstAmount = d.SgstAmount,
                    CgstPer = d.CgstPer,
                    CgstAmount = d.CgstAmount,
                    IgstPer = d.IgstPer,
                    IgstAmount = d.IgstAmount,
                    WavRate = d.WavRate,
                    TotAmnt = d.TotAmnt,
                    MtrlIssue = d.MtrlIssue
                }).ToList()
            };
        }

        private static void ApplyHeaderFields(DmsRepairBill target, DmsRepairBill source)
        {
            target.DealerName = source.DealerName;
            target.DealerCode = source.DealerCode;
            target.UniqueKey = source.UniqueKey;
            target.UniqueId = source.UniqueId;
            target.InvoiceNo = source.InvoiceNo;
            target.InvoiceDate = source.InvoiceDate;
            target.Location = source.Location;
            target.PartyName = source.PartyName;
            target.BillType = source.BillType;
            target.CashType = source.CashType;
            target.CashAccount = source.CashAccount;
            target.RegNo = source.RegNo;
            target.ChassisNo = source.ChassisNo;
        }
    }
}