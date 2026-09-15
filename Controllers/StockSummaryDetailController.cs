using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AutoGeniusSync.Controllers
{
    // Full CRUD for DMS_StockSummaryDetail (the ERP "Stock Summary Detail Report"
    // per-dealer spares/stock position). Reads are open, same as every other GET
    // controller in this codebase; every write (POST/PUT/DELETE) is gated by the
    // "Cogzim" API key, same pattern as ProformaController/RepairBillController.
    //
    // Table is upserted per (DealerCode, PartNo) — see the design note on
    // DmsStockSummaryDetail.cs. bulk-upsert is the endpoint a routine re-sync
    // should call; bulk-insert is insert-only (skips rows that already exist)
    // for a one-time/initial load.
    [ApiController]
    [Route("api/stocksummary")]
    public class StockSummaryDetailController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public StockSummaryDetailController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        private bool IsValidApiKey(string? apiKey)
        {
            var expected = _config["ApiKeys:Cogzim"];
            return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
        }

        // Matches the doc comment on DmsStockSummaryDetail.UniqueKey.
        private static string BuildUniqueKey(string? dealerCode, string partNo)
            => $"{(dealerCode ?? "").Trim().ToUpperInvariant()}|{partNo.Trim().ToUpperInvariant()}";

        // Hash of every field that can change between syncs, so bulk-upsert can
        // skip rewriting (and bumping UpdatedAt on) a row whose figures haven't moved.
        private static string ComputeRowHash(DmsStockSummaryDetail r)
        {
            var raw = string.Join('|',
                r.DealerCode, r.DealerName, r.LocCode, r.ReportOption,
                r.PartNo, r.PartDescription, r.HSNSAC_Code, r.GroupName, r.ItemType,
                r.OpenBal, r.Purchase, r.Receipt, r.PPurChln, r.Total, r.Sale,
                r.BrIss, r.MtrlIss, r.StAdj, r.PartsChln, r.PurReturn, r.SaleReturn, r.SaleChln,
                r.BalQty, r.BalAmnt, r.QtyReqd, r.MinOrder, r.BillPrice);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }

        private static DmsStockSummaryDetail MapToEntity(StockSummaryDetailDto dto) => new()
        {
            DealerCode = dto.DealerCode,
            DealerName = dto.DealerName,
            LocCode = dto.LocCode,
            ReportOption = dto.ReportOption,
            PartNo = dto.PartNo,
            PartDescription = dto.PartDescription,
            HSNSAC_Code = dto.HSNSAC_Code,
            GroupName = dto.GroupName,
            ItemType = dto.ItemType,
            OpenBal = dto.OpenBal,
            Purchase = dto.Purchase,
            Receipt = dto.Receipt,
            PPurChln = dto.PPurChln,
            Total = dto.Total,
            Sale = dto.Sale,
            BrIss = dto.BrIss,
            MtrlIss = dto.MtrlIss,
            StAdj = dto.StAdj,
            PartsChln = dto.PartsChln,
            PurReturn = dto.PurReturn,
            SaleReturn = dto.SaleReturn,
            SaleChln = dto.SaleChln,
            BalQty = dto.BalQty,
            BalAmnt = dto.BalAmnt,
            QtyReqd = dto.QtyReqd,
            MinOrder = dto.MinOrder,
            BillPrice = dto.BillPrice
        };

        // Copies every mutable business field from `source` onto `target` —
        // never touches Id/CreatedAt/UniqueKey. Shared by BulkUpsert (both the
        // fast path and its row-by-row fallback) and the single-row Update.
        private static void ApplyFields(DmsStockSummaryDetail target, DmsStockSummaryDetail source)
        {
            target.DealerCode = source.DealerCode;
            target.DealerName = source.DealerName;
            target.LocCode = source.LocCode;
            target.ReportOption = source.ReportOption;
            target.PartNo = source.PartNo;
            target.PartDescription = source.PartDescription;
            target.HSNSAC_Code = source.HSNSAC_Code;
            target.GroupName = source.GroupName;
            target.ItemType = source.ItemType;
            target.OpenBal = source.OpenBal;
            target.Purchase = source.Purchase;
            target.Receipt = source.Receipt;
            target.PPurChln = source.PPurChln;
            target.Total = source.Total;
            target.Sale = source.Sale;
            target.BrIss = source.BrIss;
            target.MtrlIss = source.MtrlIss;
            target.StAdj = source.StAdj;
            target.PartsChln = source.PartsChln;
            target.PurReturn = source.PurReturn;
            target.SaleReturn = source.SaleReturn;
            target.SaleChln = source.SaleChln;
            target.BalQty = source.BalQty;
            target.BalAmnt = source.BalAmnt;
            target.QtyReqd = source.QtyReqd;
            target.MinOrder = source.MinOrder;
            target.BillPrice = source.BillPrice;
            target.RowHash = source.RowHash;
        }

        // ---------------------------------------------------------------------
        // READ
        // ---------------------------------------------------------------------

        // GET api/stocksummary?dealerCode=...&partNo=...&groupName=...&reportOption=...&page=1&pageSize=100
        //
        // reportOption: matches the ERP screen's "Report Option" dropdown —
        // "ExcludeNegative" | "IncludeNegative" | "OnlyNegative" (case-insensitive,
        // spaces optional). Unlike the other filters this is NOT matched against
        // the stored ReportOption column (that's just traceability metadata for
        // whichever option the ERP had selected at sync time) — it's applied live
        // against BalQty, the same figure the ERP itself is filtering on, so it
        // works correctly for every row regardless of how it was synced:
        //   ExcludeNegative -> BalQty >= 0
        //   OnlyNegative    -> BalQty < 0
        //   IncludeNegative (or omitted) -> no filter, same as the ERP default
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? dealerCode,
            [FromQuery] string? partNo,
            [FromQuery] string? groupName,
            [FromQuery] string? reportOption,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 1000) pageSize = 100;

            var q = _db.DmsStockSummaryDetails.AsQueryable();

            if (!string.IsNullOrWhiteSpace(dealerCode))
                q = q.Where(x => x.DealerCode == dealerCode);
            if (!string.IsNullOrWhiteSpace(partNo))
                q = q.Where(x => x.PartNo.Contains(partNo));
            if (!string.IsNullOrWhiteSpace(groupName))
                q = q.Where(x => x.GroupName == groupName);

            if (!string.IsNullOrWhiteSpace(reportOption))
            {
                var opt = reportOption.Replace(" ", "").ToLowerInvariant();
                if (opt.Contains("exclude"))
                    q = q.Where(x => x.BalQty >= 0);
                else if (opt.Contains("only"))
                    q = q.Where(x => x.BalQty < 0);
                // "includenegative" (or anything unrecognized) -> no filter
            }

            var total = await q.CountAsync();
            var records = await q
                .OrderBy(x => x.PartNo)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
        }

        // GET api/stocksummary/5
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var row = await _db.DmsStockSummaryDetails.FindAsync(id);
            if (row == null) return NotFound(new { error = $"No stock summary row with Id {id}." });
            return Ok(row);
        }

        // ---------------------------------------------------------------------
        // WRITE — every endpoint below requires header X-API-Key: ApiKeys:Cogzim
        // ---------------------------------------------------------------------

        // POST api/stocksummary/bulk-insert
        // Insert-only: a (DealerCode, PartNo) that already exists is skipped, not
        // overwritten. Use bulk-upsert for routine re-syncs.
        [HttpPost("bulk-insert")]
        public async Task<IActionResult> BulkInsert(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromBody] List<StockSummaryDetailDto> items)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (items == null || items.Count == 0)
                return BadRequest(new { error = "No records supplied." });

            var entities = items.Select(MapToEntity).ToList();
            foreach (var e in entities)
            {
                e.UniqueKey = BuildUniqueKey(e.DealerCode, e.PartNo);
                e.RowHash = ComputeRowHash(e);
            }

            var keys = entities.Select(e => e.UniqueKey!).Distinct().ToList();
            var existingKeys = await _db.DmsStockSummaryDetails
                .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
                .Select(x => x.UniqueKey!)
                .ToListAsync();
            var existingSet = new HashSet<string>(existingKeys, StringComparer.Ordinal);

            // Also guard against duplicate (DealerCode, PartNo) pairs within the
            // same payload, so we don't try to insert the same key twice.
            var seenInBatch = new HashSet<string>(StringComparer.Ordinal);
            var toInsert = new List<DmsStockSummaryDetail>();
            var skipped = 0;
            foreach (var e in entities)
            {
                if (existingSet.Contains(e.UniqueKey!) || !seenInBatch.Add(e.UniqueKey!))
                    skipped++;
                else
                    toInsert.Add(e);
            }

            var now = DateTime.UtcNow;
            foreach (var e in toInsert) { e.CreatedAt = now; e.UpdatedAt = now; }

            int inserted = 0;
            var errors = new List<string>();
            try
            {
                await _db.DmsStockSummaryDetails.AddRangeAsync(toInsert);
                await _db.SaveChangesAsync();
                inserted = toInsert.Count;
            }
            catch (DbUpdateException)
            {
                // One bad/duplicate row shouldn't sink the whole batch — retry row by row.
                _db.ChangeTracker.Clear();
                foreach (var e in toInsert)
                {
                    try
                    {
                        _db.DmsStockSummaryDetails.Add(e);
                        await _db.SaveChangesAsync();
                        inserted++;
                    }
                    catch (Exception rowEx)
                    {
                        _db.ChangeTracker.Clear();
                        errors.Add($"{e.DealerCode}/{e.PartNo}: {rowEx.Message}");
                    }
                }
            }

            return Ok(new { Inserted = inserted, Skipped = skipped, Errors = errors.Take(20) });
        }

        // PUT api/stocksummary/bulk-upsert
        // Insert new (DealerCode, PartNo) rows, overwrite existing ones with the
        // latest figures. This is the endpoint a routine stock re-sync should call.
        [HttpPut("bulk-upsert")]
        public async Task<IActionResult> BulkUpsert(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromBody] List<StockSummaryDetailDto> items)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (items == null || items.Count == 0)
                return BadRequest(new { error = "No records supplied." });

            var incoming = items.Select(MapToEntity).ToList();
            foreach (var e in incoming)
            {
                e.UniqueKey = BuildUniqueKey(e.DealerCode, e.PartNo);
                e.RowHash = ComputeRowHash(e);
            }

            var keys = incoming.Select(e => e.UniqueKey!).Distinct().ToList();
            var existingByKey = await _db.DmsStockSummaryDetails
                .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
                .ToDictionaryAsync(x => x.UniqueKey!, StringComparer.Ordinal);

            int inserted = 0, updated = 0, unchanged = 0;
            var errors = new List<string>();
            var now = DateTime.UtcNow;

            foreach (var e in incoming)
            {
                if (existingByKey.TryGetValue(e.UniqueKey!, out var row))
                {
                    if (row.RowHash == e.RowHash) { unchanged++; continue; }
                    ApplyFields(row, e);
                    row.UpdatedAt = now;
                    updated++;
                }
                else
                {
                    e.CreatedAt = now;
                    e.UpdatedAt = now;
                    _db.DmsStockSummaryDetails.Add(e);
                    existingByKey[e.UniqueKey!] = e; // guard against dupe keys within this payload
                    inserted++;
                }
            }

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Fall back to row-by-row so one bad row doesn't sink the batch.
                _db.ChangeTracker.Clear();
                inserted = 0; updated = 0; unchanged = 0;
                foreach (var e in incoming)
                {
                    try
                    {
                        var row = await _db.DmsStockSummaryDetails
                            .FirstOrDefaultAsync(x => x.UniqueKey == e.UniqueKey);
                        if (row == null)
                        {
                            e.CreatedAt = now;
                            e.UpdatedAt = now;
                            _db.DmsStockSummaryDetails.Add(e);
                            await _db.SaveChangesAsync();
                            inserted++;
                        }
                        else if (row.RowHash != e.RowHash)
                        {
                            ApplyFields(row, e);
                            row.UpdatedAt = now;
                            await _db.SaveChangesAsync();
                            updated++;
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    catch (Exception rowEx)
                    {
                        _db.ChangeTracker.Clear();
                        errors.Add($"{e.DealerCode}/{e.PartNo}: {rowEx.Message}");
                    }
                }
            }

            return Ok(new { Inserted = inserted, Updated = updated, Unchanged = unchanged, Errors = errors.Take(20) });
        }

        // PUT api/stocksummary/5
        // Single-row update by Id.
        [HttpPut("{id:int}")]
        public async Task<IActionResult> Update(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            int id,
            [FromBody] StockSummaryDetailDto dto)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            var row = await _db.DmsStockSummaryDetails.FindAsync(id);
            if (row == null) return NotFound(new { error = $"No stock summary row with Id {id}." });

            var updated = MapToEntity(dto);
            updated.UniqueKey = BuildUniqueKey(updated.DealerCode, updated.PartNo);
            updated.RowHash = ComputeRowHash(updated);

            ApplyFields(row, updated);
            row.UniqueKey = updated.UniqueKey;
            row.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(row);
        }

        // DELETE api/stocksummary/5
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            int id)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            var row = await _db.DmsStockSummaryDetails.FindAsync(id);
            if (row == null) return NotFound(new { error = $"No stock summary row with Id {id}." });

            _db.DmsStockSummaryDetails.Remove(row);
            await _db.SaveChangesAsync();
            return Ok(new { Deleted = id });
        }

        // DELETE api/stocksummary/bulk-delete
        // Body: { "ids": [1,2,3] }  OR  { "dealerCode": "D001" } — the latter wipes
        // that dealer's whole stock snapshot, e.g. right before re-importing it fresh.
        [HttpDelete("bulk-delete")]
        public async Task<IActionResult> BulkDelete(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromBody] BulkDeleteRequestDto? request)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (request == null || ((request.Ids == null || request.Ids.Count == 0) && string.IsNullOrWhiteSpace(request.DealerCode)))
                return BadRequest(new { error = "Provide ids or dealerCode." });

            var q = _db.DmsStockSummaryDetails.AsQueryable();
            q = (request.Ids != null && request.Ids.Count > 0)
                ? q.Where(x => request.Ids.Contains(x.Id))
                : q.Where(x => x.DealerCode == request.DealerCode);

            var rows = await q.ToListAsync();
            _db.DmsStockSummaryDetails.RemoveRange(rows);
            await _db.SaveChangesAsync();

            return Ok(new { Deleted = rows.Count });
        }
    }
}