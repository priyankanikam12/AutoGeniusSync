using AutoGeniusSync.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AutoGeniusSync.Models;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/partner/anv")]
public class AnvPartnerController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private const string NameFilter = "%ANV%";

    public AnvPartnerController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsValidApiKey(string? apiKey)
    {
        var expected = _config["ApiKeys:ANVWeb"];
        return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
    }

    private async Task<List<string>> GetAnvChassisNumbersAsync()
    {
        var jr = _db.DmsJobReports
            .Where(x => x.ChassisNo != null && x.CustomerName != null && EF.Functions.Like(x.CustomerName, NameFilter))
            .Select(x => x.ChassisNo!);

        var pf = _db.Proformas
            .Where(x => x.ChassisNo != null && x.PartyName != null && EF.Functions.Like(x.PartyName, NameFilter))
            .Select(x => x.ChassisNo!);

        var rb = _db.RepairBills
            .Where(x => x.ChassisNo != null && x.PartyName != null && EF.Functions.Like(x.PartyName, NameFilter))
            .Select(x => x.ChassisNo!);

        return await jr.Union(pf).Union(rb).Distinct().ToListAsync();
    }

    // ═══════════════════════════════════════════════════════
    // METHOD 1 — DMS_JobReport + DMS_JobReportComplaints
    // Same entity graph (1 JobReport → many Complaints, FK JobReportId).
    // Every field on both tables returned as-is.
    // ═══════════════════════════════════════════════════════
    [HttpGet("jobreports")]
    public async Task<IActionResult> GetJobReports(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] bool includeAuditLog = false)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        var chassisNumbers = await GetAnvChassisNumbersAsync();
        if (chassisNumbers.Count == 0)
            return Ok(new { Total = 0, Records = Array.Empty<object>() });

        var records = await _db.DmsJobReports
            .Include(x => x.Complaints)                       // DMS_JobReportComplaints, every field
            .Where(x => x.ChassisNo != null && chassisNumbers.Contains(x.ChassisNo))
            .OrderByDescending(x => x.JobDate)
            .ToListAsync();

        if (!includeAuditLog)
            foreach (var r in records) r.Action = null;        // internal write-history, off by default

        return Ok(new { Total = records.Count, Records = records });
    }

    // ═══════════════════════════════════════════════════════
    // METHOD 2 — DMS_MaterialTransfer + DMS_MaterialTransferItem + DMS_MaterialTransferLabor
    // MT → Item is 1:many (FK MaterialTransferId); Item → Labor is 1:many (FK MaterialTransferItemId).
    // "match data in JobReport" — UNVERIFIED. No ChassisNo/PartyName exists on
    // DMS_MaterialTransfer, so this can only attempt SourceJobId == Proforma/
    // RepairBill.UniqueKey, then borrow that record's ChassisNo to line up with
    // JobReport. Gated behind confirmMatch=true — see notes in prior messages.
    // ═══════════════════════════════════════════════════════
    [HttpGet("materialtransfers")]
    public async Task<IActionResult> GetMaterialTransfers(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] bool confirmMatch = false,
        [FromQuery] bool includeAuditLog = false)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        if (!confirmMatch)
        {
            return StatusCode(501, new
            {
                error = "MaterialTransfer-to-JobReport match not confirmed.",
                reason = "DMS_MaterialTransfer has no ChassisNo/PartyName column. The only candidate link — SourceJobId compared to Proforma/RepairBill.UniqueKey — is unverified against real data.",
                howToEnable = "Run the verification SELECTs shared earlier, confirm a real match rate, then call with ?confirmMatch=true."
            });
        }

        var chassisNumbers = await GetAnvChassisNumbersAsync();
        if (chassisNumbers.Count == 0)
            return Ok(new { Total = 0, Records = Array.Empty<object>() });

        // Build SourceJobId -> ChassisNo via Proforma/RepairBill UniqueKey (the unverified bridge)
        var jobIdToChassis = new Dictionary<long, string>();

        var pfKeys = await _db.Proformas
            .Where(p => p.ChassisNo != null && chassisNumbers.Contains(p.ChassisNo) && p.UniqueKey.HasValue)
            .Select(p => new { p.UniqueKey, p.ChassisNo })
            .ToListAsync();
        foreach (var p in pfKeys) jobIdToChassis[p.UniqueKey!.Value] = p.ChassisNo!;

        var rbKeys = await _db.RepairBills
            .Where(r => r.ChassisNo != null && chassisNumbers.Contains(r.ChassisNo) && r.UniqueKey.HasValue)
            .Select(r => new { r.UniqueKey, r.ChassisNo })
            .ToListAsync();
        foreach (var r in rbKeys) jobIdToChassis[r.UniqueKey!.Value] = r.ChassisNo!;

        var candidateIds = jobIdToChassis.Keys.ToList();

        var transfers = await _db.DmsMaterialTransfers
            .Include(x => x.Items).ThenInclude(i => i.LabourLines)   // DMS_MaterialTransferItem + DMS_MaterialTransferLabor, every field
            .Where(x => x.SourceJobId != null && candidateIds.Contains(x.SourceJobId.Value))
            .OrderByDescending(x => x.DocDate)
            .ToListAsync();

        if (!includeAuditLog)
            foreach (var mt in transfers) mt.Action = null;

        // Attach the matched JobReport(s) for the resolved chassis, per transfer
        var chassisForTransfers = transfers
            .Where(mt => mt.SourceJobId.HasValue && jobIdToChassis.ContainsKey(mt.SourceJobId.Value))
            .Select(mt => jobIdToChassis[mt.SourceJobId!.Value])
            .Distinct()
            .ToList();

        var jobReports = await _db.DmsJobReports
            .Include(x => x.Complaints)
            .Where(x => x.ChassisNo != null && chassisForTransfers.Contains(x.ChassisNo))
            .ToListAsync();

        if (!includeAuditLog)
            foreach (var jr in jobReports) jr.Action = null;

        var results = transfers.Select(mt =>
        {
            var chassis = mt.SourceJobId.HasValue && jobIdToChassis.TryGetValue(mt.SourceJobId.Value, out var c) ? c : null;
            return new
            {
                MaterialTransfer = mt,          // full entity incl. Items -> LabourLines
                MatchedChassisNo = chassis,
                MatchBasis = "UNVERIFIED — SourceJobId matched against Proforma/RepairBill.UniqueKey",
                MatchedJobReports = chassis != null
                    ? jobReports.Where(jr => jr.ChassisNo == chassis)
                    : Enumerable.Empty<object>()
            };
        });

        return Ok(new { Total = transfers.Count, Records = results });
    }

    // ═══════════════════════════════════════════════════════
    // METHOD 3 — DMS_Proforma + DMS_ProformaItem
    // Proforma → Item is 1:many (FK ProformaId).
    // "match data in JobReport + MaterialTransfer": JobReport via confirmed
    // ChassisNo; MaterialTransfer via the same unverified UniqueKey bridge as
    // Method 2, gated the same way.
    // ═══════════════════════════════════════════════════════
    [HttpGet("proformas")]
    public async Task<IActionResult> GetProformas(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] bool confirmMaterialTransferMatch = false,
        [FromQuery] bool includeAuditLog = false)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        var chassisNumbers = await GetAnvChassisNumbersAsync();
        if (chassisNumbers.Count == 0)
            return Ok(new { Total = 0, Records = Array.Empty<object>() });

        var proformas = await _db.Proformas
            .Include(p => p.Items)                              // DMS_ProformaItem, every field
            .Where(p => p.ChassisNo != null && chassisNumbers.Contains(p.ChassisNo))
            .OrderByDescending(p => p.Id)
            .ToListAsync();

        var jobReports = await _db.DmsJobReports
            .Include(x => x.Complaints)
            .Where(x => x.ChassisNo != null && chassisNumbers.Contains(x.ChassisNo))
            .ToListAsync();

        if (!includeAuditLog)
            foreach (var jr in jobReports) jr.Action = null;

        Dictionary<long, List<DmsMaterialTransfer>> mtByUniqueKey = new();
        if (confirmMaterialTransferMatch)
        {
            var uniqueKeys = proformas.Where(p => p.UniqueKey.HasValue).Select(p => p.UniqueKey!.Value).ToList();
            var transfers = await _db.DmsMaterialTransfers
                .Include(x => x.Items).ThenInclude(i => i.LabourLines)
                .Where(x => x.SourceJobId != null && uniqueKeys.Contains(x.SourceJobId.Value))
                .ToListAsync();

            if (!includeAuditLog)
                foreach (var mt in transfers) mt.Action = null;

            foreach (var mt in transfers)
            {
                if (!mtByUniqueKey.TryGetValue(mt.SourceJobId!.Value, out var list))
                    mtByUniqueKey[mt.SourceJobId!.Value] = list = new List<DmsMaterialTransfer>();
                list.Add(mt);
            }
        }

        var results = proformas.Select(pf => new
        {
            Proforma = pf,                                                       // full entity incl. Items
            MatchedJobReports = jobReports.Where(jr => jr.ChassisNo == pf.ChassisNo),
            MatchedMaterialTransfers = confirmMaterialTransferMatch
                ? (pf.UniqueKey.HasValue && mtByUniqueKey.TryGetValue(pf.UniqueKey.Value, out var mts) ? mts : new List<DmsMaterialTransfer>())
                : null,
            MaterialTransferStatus = confirmMaterialTransferMatch
                ? "Included via unverified SourceJobId==UniqueKey match"
                : "Not requested. Pass confirmMaterialTransferMatch=true to attempt the unverified match."
        });

        return Ok(new { Total = proformas.Count, Records = results });
    }

    // ═══════════════════════════════════════════════════════
    // METHOD 4 — DMS_RepairBill + DMS_RepairBillItem
    // RepairBill → Item is 1:many (FK RepairBillId).
    // "match data JobReport + MaterialTransfer + Proforma": JobReport and
    // Proforma via confirmed ChassisNo; MaterialTransfer via the same
    // unverified bridge, same gate.
    // ═══════════════════════════════════════════════════════
    [HttpGet("repairbills")]
    public async Task<IActionResult> GetRepairBills(
        [FromHeader(Name = "X-API-Key")] string? apiKey,
        [FromQuery] bool confirmMaterialTransferMatch = false,
        [FromQuery] bool includeAuditLog = false)
    {
        if (!IsValidApiKey(apiKey))
            return Unauthorized(new { error = "Invalid or missing API key." });

        var chassisNumbers = await GetAnvChassisNumbersAsync();
        if (chassisNumbers.Count == 0)
            return Ok(new { Total = 0, Records = Array.Empty<object>() });

        var repairBills = await _db.RepairBills
            .Include(x => x.Items)                              // DMS_RepairBillItem, every field
            .Where(x => x.ChassisNo != null && chassisNumbers.Contains(x.ChassisNo))
            .OrderByDescending(x => x.Id)
            .ToListAsync();

        var jobReports = await _db.DmsJobReports
            .Include(x => x.Complaints)
            .Where(x => x.ChassisNo != null && chassisNumbers.Contains(x.ChassisNo))
            .ToListAsync();

        if (!includeAuditLog)
            foreach (var jr in jobReports) jr.Action = null;

        var proformas = await _db.Proformas
            .Include(p => p.Items)
            .Where(p => p.ChassisNo != null && chassisNumbers.Contains(p.ChassisNo))
            .ToListAsync();

        Dictionary<long, List<DmsMaterialTransfer>> mtByUniqueKey = new();
        if (confirmMaterialTransferMatch)
        {
            var uniqueKeys = repairBills.Where(r => r.UniqueKey.HasValue).Select(r => r.UniqueKey!.Value).ToList();
            var transfers = await _db.DmsMaterialTransfers
                .Include(x => x.Items).ThenInclude(i => i.LabourLines)
                .Where(x => x.SourceJobId != null && uniqueKeys.Contains(x.SourceJobId.Value))
                .ToListAsync();

            if (!includeAuditLog)
                foreach (var mt in transfers) mt.Action = null;

            foreach (var mt in transfers)
            {
                if (!mtByUniqueKey.TryGetValue(mt.SourceJobId!.Value, out var list))
                    mtByUniqueKey[mt.SourceJobId!.Value] = list = new List<DmsMaterialTransfer>();
                list.Add(mt);
            }
        }

        var results = repairBills.Select(rb => new
        {
            RepairBill = rb,                                                      // full entity incl. Items
            MatchedJobReports = jobReports.Where(jr => jr.ChassisNo == rb.ChassisNo),
            MatchedProformas = proformas.Where(pf => pf.ChassisNo == rb.ChassisNo),
            MatchedMaterialTransfers = confirmMaterialTransferMatch
                ? (rb.UniqueKey.HasValue && mtByUniqueKey.TryGetValue(rb.UniqueKey.Value, out var mts) ? mts : new List<DmsMaterialTransfer>())
                : null,
            MaterialTransferStatus = confirmMaterialTransferMatch
                ? "Included via unverified SourceJobId==UniqueKey match"
                : "Not requested. Pass confirmMaterialTransferMatch=true to attempt the unverified match."
        });

        return Ok(new { Total = repairBills.Count, Records = results });
    }
}