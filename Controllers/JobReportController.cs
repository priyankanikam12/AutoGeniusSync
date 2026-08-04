using AutoGeniusSync.Data;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/jobreport")]
public class JobReportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<JobReportController> _logger;

    public JobReportController(AppDbContext db, ILogger<JobReportController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static string BuildKey(string? dealerCode, string? jobNo, DateOnly? jobDate, string? chassisNo)
        => $"{dealerCode?.Trim().ToUpperInvariant()}{jobNo?.Trim().ToUpperInvariant()}{jobDate?.ToString("yyyy-MM-dd")}{chassisNo?.Trim().ToUpperInvariant()}";

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        var formats = new[] { "dd-MM-yyyy", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
        foreach (var fmt in formats)
        {
            if (DateTime.TryParseExact(val.Trim(), fmt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
                return DateOnly.FromDateTime(d);
        }
        if (DateTime.TryParse(val, out var d2))
            return DateOnly.FromDateTime(d2);
        return null;
    }

    private static DmsJobReport MapRow(string[] cols)
    {
        // Column order matches the header exactly:
        // Dealer Name, Dealer Code, Dealer location, City, State, Job No.,
        // Job Date, Job Type, Service Head, Service Type, KMS, Customer Name,
        // Mobile no., Chassis No., Reg No., Engine No., Item Name,
        // Customer Voice, Complaint Code, Observation, Supervisor Comment,
        // Job Status, Battery No, Brand Name, Charger No, Sale Date,
        // Supervisor, Technician, Job End Date, Created Through
        string? Get(int i) => i < cols.Length ? cols[i].Trim() : null;

        var dealerCode = Get(1);
        var jobNo      = Get(5);
        var jobDate    = ParseDate(Get(6));
        var chassisNo  = Get(13);

        return new DmsJobReport
        {
            DealerName        = Get(0),
            DealerCode        = dealerCode,
            DealerLocation    = Get(2),
            City              = Get(3),
            State             = Get(4),
            JobNo             = jobNo,
            JobDate           = jobDate,
            JobType           = Get(7),
            ServiceHead       = Get(8),
            ServiceType       = Get(9),
            Kms               = Get(10),
            CustomerName      = Get(11),
            MobileNo          = Get(12),
            ChassisNo         = chassisNo,
            RegNo             = Get(14),
            EngineNo          = Get(15),
            ItemName          = Get(16),
            CustomerVoice     = Get(17),
            ComplaintCode     = Get(18),
            Observation       = Get(19),
            SupervisorComment = Get(20),
            JobStatus         = Get(21),
            BatteryNo         = Get(22),
            BrandName         = Get(23),
            ChargerNo         = Get(24),
            SaleDate          = ParseDate(Get(25)),
            Supervisor        = Get(26),
            Technician        = Get(27),
            JobEndDate        = ParseDate(Get(28)),
            CreatedThrough    = Get(29),
            UniqueKey         = BuildKey(dealerCode, jobNo, jobDate, chassisNo),
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        };
    }

    // ═══════════════════════════════════════════════════════
    // FILE UPLOAD ENDPOINTS — unchanged, kept as-is
    // ═══════════════════════════════════════════════════════

    // POST /api/jobreport/upload — INSERT ONLY, skips existing rows
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        int inserted = 0, skippedAsExisting = 0, skippedOnError = 0;
        var errors = new List<string>();

        using var reader = new StreamReader(file.OpenReadStream());
        var headerLine = await reader.ReadLineAsync();
        var delimiter = headerLine != null && headerLine.Contains('\t') ? '\t' : ',';

        var rows = new List<DmsJobReport>();
        int lineNo = 1;

        while (!reader.EndOfStream)
        {
            lineNo++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var cols = line.Split(delimiter);
                var row = MapRow(cols);
                if (string.IsNullOrEmpty(row.UniqueKey) || row.UniqueKey.Length < 4)
                {
                    skippedOnError++;
                    continue;
                }
                rows.Add(row);
            }
            catch (Exception ex)
            {
                skippedOnError++;
                errors.Add($"Line {lineNo}: {ex.Message}");
            }
        }

        var dedupedRows = rows
            .GroupBy(r => r.UniqueKey)
            .Select(g => g.Last())
            .ToList();

        var candidateKeys = dedupedRows.Select(r => r.UniqueKey).ToHashSet();
        var existingKeys = (await _db.DmsJobReports
            .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        foreach (var row in dedupedRows)
        {
            if (existingKeys.Contains(row.UniqueKey!))
            {
                skippedAsExisting++;
                continue;
            }

            _db.DmsJobReports.Add(row);
            inserted++;
            existingKeys.Add(row.UniqueKey!);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "JobReport upload (insert-only): {ins} inserted, {skip} skipped (already exist), {err} error(s)",
            inserted, skippedAsExisting, skippedOnError);

        return Ok(new
        {
            Inserted = inserted,
            SkippedAsExisting = skippedAsExisting,
            SkippedOnError = skippedOnError,
            Errors = errors.Take(20)
        });
    }

    // PUT /api/jobreport/upload — UPSERT: insert new, update existing
    [HttpPut("upload")]
    public async Task<IActionResult> UpsertUpload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        int inserted = 0, updated = 0, skippedOnError = 0;
        var errors = new List<string>();

        using var reader = new StreamReader(file.OpenReadStream());
        var headerLine = await reader.ReadLineAsync();
        var delimiter = headerLine != null && headerLine.Contains('\t') ? '\t' : ',';

        var rows = new List<DmsJobReport>();
        int lineNo = 1;

        while (!reader.EndOfStream)
        {
            lineNo++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var cols = line.Split(delimiter);
                var row = MapRow(cols);
                if (string.IsNullOrEmpty(row.UniqueKey) || row.UniqueKey.Length < 4)
                {
                    skippedOnError++;
                    continue;
                }
                rows.Add(row);
            }
            catch (Exception ex)
            {
                skippedOnError++;
                errors.Add($"Line {lineNo}: {ex.Message}");
            }
        }

        var dedupedRows = rows
            .GroupBy(r => r.UniqueKey)
            .Select(g => g.Last())
            .ToList();

        var candidateKeys = dedupedRows.Select(r => r.UniqueKey).ToHashSet();
        var existingRows = await _db.DmsJobReports
            .Where(x => x.UniqueKey != null && candidateKeys.Contains(x.UniqueKey))
            .ToListAsync();
        var existingLookup = existingRows
            .Where(x => x.UniqueKey != null)
            .GroupBy(x => x.UniqueKey!)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var row in dedupedRows)
        {
            try
            {
                if (existingLookup.TryGetValue(row.UniqueKey!, out var existing))
                {
                    existing.DealerName        = row.DealerName;
                    existing.DealerLocation    = row.DealerLocation;
                    existing.City              = row.City;
                    existing.State             = row.State;
                    existing.JobType           = row.JobType;
                    existing.ServiceHead       = row.ServiceHead;
                    existing.ServiceType       = row.ServiceType;
                    existing.Kms               = row.Kms;
                    existing.CustomerName      = row.CustomerName;
                    existing.MobileNo          = row.MobileNo;
                    existing.RegNo             = row.RegNo;
                    existing.EngineNo          = row.EngineNo;
                    existing.ItemName          = row.ItemName;
                    existing.CustomerVoice     = row.CustomerVoice;
                    existing.ComplaintCode     = row.ComplaintCode;
                    existing.Observation       = row.Observation;
                    existing.SupervisorComment = row.SupervisorComment;
                    existing.JobStatus         = row.JobStatus;
                    existing.BatteryNo         = row.BatteryNo;
                    existing.BrandName         = row.BrandName;
                    existing.ChargerNo         = row.ChargerNo;
                    existing.SaleDate          = row.SaleDate;
                    existing.Supervisor        = row.Supervisor;
                    existing.Technician        = row.Technician;
                    existing.JobEndDate        = row.JobEndDate;
                    existing.CreatedThrough    = row.CreatedThrough;
                    existing.UpdatedAt         = DateTime.UtcNow;
                    updated++;
                }
                else
                {
                    _db.DmsJobReports.Add(row);
                    inserted++;
                }
            }
            catch (Exception ex)
            {
                skippedOnError++;
                _logger.LogWarning("Upsert skip job {no}: {msg}", row.JobNo, ex.Message);
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "JobReport upload (upsert): {ins} inserted, {upd} updated, {err} error(s)",
            inserted, updated, skippedOnError);

        return Ok(new
        {
            Inserted = inserted,
            Updated = updated,
            SkippedOnError = skippedOnError,
            Errors = errors.Take(20)
        });
    }

    // ═══════════════════════════════════════════════════════
    // STANDARD CRUD — single-record JSON API (new)
    // ═══════════════════════════════════════════════════════

    // GET /api/jobreport — list/filter (unchanged)
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? dealerCode = null,
        [FromQuery] string? chassisNo = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        var q = _db.DmsJobReports.AsQueryable();

        if (!string.IsNullOrEmpty(dealerCode))
            q = q.Where(x => x.DealerCode == dealerCode);

        if (!string.IsNullOrEmpty(chassisNo))
            q = q.Where(x => x.ChassisNo == chassisNo);

        var total = await q.CountAsync();
        var records = await q
            .OrderByDescending(x => x.JobDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
    }

    // GET /api/jobreport/{id} — read a single record by Id
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var row = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.Id == id);
        return row == null ? NotFound(new { error = $"JobReport with Id {id} not found." }) : Ok(row);
    }

    // POST /api/jobreport — CREATE a single record from a JSON body.
    // Rejects if a record with the same natural key (DealerCode+JobNo+
    // JobDate+ChassisNo) already exists — use PUT to update instead.
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DmsJobReport input)
    {
        if (input == null)
            return BadRequest(new { error = "Request body is required." });

        input.UniqueKey = BuildKey(input.DealerCode, input.JobNo, input.JobDate, input.ChassisNo);

        if (string.IsNullOrEmpty(input.UniqueKey) || input.UniqueKey.Length < 4)
            return BadRequest(new { error = "DealerCode, JobNo, JobDate, and ChassisNo are required to create a record." });

        var existing = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.UniqueKey == input.UniqueKey);
        if (existing != null)
            return Conflict(new { error = "A record with this DealerCode+JobNo+JobDate+ChassisNo already exists.", ExistingId = existing.Id });

        input.Id = 0; // ensure EF treats this as a new row, ignoring any client-supplied Id
        input.CreatedAt = DateTime.UtcNow;
        input.UpdatedAt = DateTime.UtcNow;

        _db.DmsJobReports.Add(input);
        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport created: Id {id}, Job {jn}", input.Id, input.JobNo);

        return CreatedAtAction(nameof(GetById), new { id = input.Id }, input);
    }

    // PUT /api/jobreport/{id} — UPDATE (full replace) an existing record by Id
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DmsJobReport input)
    {
        if (input == null)
            return BadRequest(new { error = "Request body is required." });

        var existing = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null)
            return NotFound(new { error = $"JobReport with Id {id} not found." });

        existing.DealerName        = input.DealerName;
        existing.DealerCode        = input.DealerCode;
        existing.DealerLocation    = input.DealerLocation;
        existing.City              = input.City;
        existing.State             = input.State;
        existing.JobNo             = input.JobNo;
        existing.JobDate           = input.JobDate;
        existing.JobType           = input.JobType;
        existing.ServiceHead       = input.ServiceHead;
        existing.ServiceType       = input.ServiceType;
        existing.Kms               = input.Kms;
        existing.CustomerName      = input.CustomerName;
        existing.MobileNo          = input.MobileNo;
        existing.ChassisNo         = input.ChassisNo;
        existing.RegNo             = input.RegNo;
        existing.EngineNo          = input.EngineNo;
        existing.ItemName          = input.ItemName;
        existing.CustomerVoice     = input.CustomerVoice;
        existing.ComplaintCode     = input.ComplaintCode;
        existing.Observation       = input.Observation;
        existing.SupervisorComment = input.SupervisorComment;
        existing.JobStatus         = input.JobStatus;
        existing.BatteryNo         = input.BatteryNo;
        existing.BrandName         = input.BrandName;
        existing.ChargerNo         = input.ChargerNo;
        existing.SaleDate          = input.SaleDate;
        existing.Supervisor        = input.Supervisor;
        existing.Technician        = input.Technician;
        existing.JobEndDate        = input.JobEndDate;
        existing.CreatedThrough    = input.CreatedThrough;
        existing.UniqueKey         = BuildKey(input.DealerCode, input.JobNo, input.JobDate, input.ChassisNo);
        existing.UpdatedAt         = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport updated: Id {id}, Job {jn}", id, existing.JobNo);

        return Ok(existing);
    }

    // PATCH /api/jobreport/{id} — partial update: only overwrite fields
    // the caller actually included in the request body (any left as
    // their default in the JSON payload are left untouched on the
    // existing record). Uses a plain object dictionary rather than
    // DmsJobReport so "field not present" and "field explicitly null"
    // can be distinguished.
    [HttpPatch("{id:int}")]
    public async Task<IActionResult> PartialUpdate(int id, [FromBody] Dictionary<string, object?> updates)
    {
        var existing = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null)
            return NotFound(new { error = $"JobReport with Id {id} not found." });

        foreach (var kvp in updates)
        {
            var value = kvp.Value?.ToString();
            switch (kvp.Key)
            {
                case "DealerName":        existing.DealerName = value; break;
                case "DealerCode":        existing.DealerCode = value; break;
                case "DealerLocation":    existing.DealerLocation = value; break;
                case "City":              existing.City = value; break;
                case "State":             existing.State = value; break;
                case "JobNo":             existing.JobNo = value; break;
                case "JobDate":           existing.JobDate = ParseDate(value); break;
                case "JobType":           existing.JobType = value; break;
                case "ServiceHead":       existing.ServiceHead = value; break;
                case "ServiceType":       existing.ServiceType = value; break;
                case "Kms":               existing.Kms = value; break;
                case "CustomerName":      existing.CustomerName = value; break;
                case "MobileNo":          existing.MobileNo = value; break;
                case "ChassisNo":         existing.ChassisNo = value; break;
                case "RegNo":             existing.RegNo = value; break;
                case "EngineNo":          existing.EngineNo = value; break;
                case "ItemName":          existing.ItemName = value; break;
                case "CustomerVoice":     existing.CustomerVoice = value; break;
                case "ComplaintCode":     existing.ComplaintCode = value; break;
                case "Observation":       existing.Observation = value; break;
                case "SupervisorComment": existing.SupervisorComment = value; break;
                case "JobStatus":         existing.JobStatus = value; break;
                case "BatteryNo":         existing.BatteryNo = value; break;
                case "BrandName":         existing.BrandName = value; break;
                case "ChargerNo":         existing.ChargerNo = value; break;
                case "SaleDate":          existing.SaleDate = ParseDate(value); break;
                case "Supervisor":        existing.Supervisor = value; break;
                case "Technician":        existing.Technician = value; break;
                case "JobEndDate":        existing.JobEndDate = ParseDate(value); break;
                case "CreatedThrough":    existing.CreatedThrough = value; break;
                // Id, UniqueKey, CreatedAt, UpdatedAt are intentionally
                // NOT settable via PATCH — they're system-managed.
                default:
                    _logger.LogWarning("PATCH JobReport {id}: unknown field '{field}' ignored", id, kvp.Key);
                    break;
            }
        }

        // Recompute the natural key in case any of its component fields changed.
        existing.UniqueKey = BuildKey(existing.DealerCode, existing.JobNo, existing.JobDate, existing.ChassisNo);
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport patched: Id {id}, fields: {fields}", id, string.Join(",", updates.Keys));

        return Ok(existing);
    }

    // DELETE /api/jobreport/{id} — permanently removes one record
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var existing = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.Id == id);
        if (existing == null)
            return NotFound(new { error = $"JobReport with Id {id} not found." });

        _db.DmsJobReports.Remove(existing);
        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport deleted: Id {id}, Job {jn}", id, existing.JobNo);

        return NoContent();
    }
}