using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.Helpers;

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
    // FILE UPLOAD ENDPOINTS — unchanged
    // ═══════════════════════════════════════════════════════

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
    // STANDARD CRUD — single-record JSON API
    // ═══════════════════════════════════════════════════════

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

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var row = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.Id == id);
        return row == null ? NotFound(new { error = $"JobReport with Id {id} not found." }) : Ok(row);
    }

    // POST /api/jobreport — CREATE. Body has NO Id field — the client
    // never sends one, since Id is generated by the database.
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JobReportInput input)
    {
        if (input == null)
            return BadRequest(new { error = "Request body is required." });

        var uniqueKey = BuildKey(input.DealerCode, input.JobNo, input.JobDate, input.ChassisNo);

        if (string.IsNullOrEmpty(uniqueKey) || uniqueKey.Length < 4)
            return BadRequest(new { error = "DealerCode, JobNo, JobDate, and ChassisNo are required to create a record." });

        var existing = await _db.DmsJobReports.FirstOrDefaultAsync(x => x.UniqueKey == uniqueKey);
        if (existing != null)
            return Conflict(new { error = "A record with this DealerCode+JobNo+JobDate+ChassisNo already exists.", ExistingId = existing.Id });

        var entity = new DmsJobReport
        {
            DealerName        = input.DealerName,
            DealerCode        = input.DealerCode,
            DealerLocation    = input.DealerLocation,
            City              = input.City,
            State             = input.State,
            JobNo             = input.JobNo,
            JobDate           = input.JobDate,
            JobType           = input.JobType,
            ServiceHead       = input.ServiceHead,
            ServiceType       = input.ServiceType,
            Kms               = input.Kms,
            CustomerName      = input.CustomerName,
            MobileNo          = input.MobileNo,
            ChassisNo         = input.ChassisNo,
            RegNo             = input.RegNo,
            EngineNo          = input.EngineNo,
            ItemName          = input.ItemName,
            CustomerVoice     = input.CustomerVoice,
            ComplaintCode     = input.ComplaintCode,
            Observation       = input.Observation,
            SupervisorComment = input.SupervisorComment,
            JobStatus         = input.JobStatus,
            BatteryNo         = input.BatteryNo,
            BrandName         = input.BrandName,
            ChargerNo         = input.ChargerNo,
            SaleDate          = input.SaleDate,
            Supervisor        = input.Supervisor,
            Technician        = input.Technician,
            JobEndDate        = input.JobEndDate,
            CreatedThrough    = input.CreatedThrough,
            UniqueKey         = uniqueKey,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
            // Id is NOT set — the database generates it on insert.
        };

        _db.DmsJobReports.Add(entity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport created: Id {id}, Job {jn}", entity.Id, entity.JobNo);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, entity);
    }

    // PUT /api/jobreport/{id} — full update. Id comes from the ROUTE,
    // never from the body — the body has no Id field to send.
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] JobReportInput input)
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

    // PATCH /api/jobreport/{id} — partial update, unchanged
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
                default:
                    _logger.LogWarning("PATCH JobReport {id}: unknown field '{field}' ignored", id, kvp.Key);
                    break;
            }
        }

        existing.UniqueKey = BuildKey(existing.DealerCode, existing.JobNo, existing.JobDate, existing.ChassisNo);
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("JobReport patched: Id {id}, fields: {fields}", id, string.Join(",", updates.Keys));

        return Ok(existing);
    }

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

    // ── POST /api/jobreport/bulk — insert-only ──
    [HttpPost("bulk")]
    public async Task<IActionResult> BulkInsert([FromBody] List<JobReportRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.JobReport(r.DealerCode, r.JobNo, r.JobDate, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existingKeys = (await _db.DmsJobReports
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .Select(x => x.UniqueKey!)
            .ToListAsync()).ToHashSet();

        var toInsert = new List<DmsJobReport>();
        var skipped = new List<string>();

        foreach (var r in deduped)
        {
            if (existingKeys.Contains(r.UniqueKey!)) { skipped.Add(r.UniqueKey!); continue; }
            toInsert.Add(MapJobReport(r));
        }

        if (toInsert.Count > 0)
        {
            await _db.DmsJobReports.AddRangeAsync(toInsert);
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("JobReport bulk insert: {ins} inserted, {skip} skipped", toInsert.Count, skipped.Count);
        return Ok(new { Inserted = toInsert.Count, SkippedDuplicates = skipped.Count, SkippedKeys = skipped });
    }

    // ── PUT /api/jobreport/bulk — upsert ──
    [HttpPut("bulk")]
    public async Task<IActionResult> BulkUpsert([FromBody] List<JobReportRecordDto> records)
    {
        if (records is null || records.Count == 0)
            return BadRequest(new { error = "Payload must be a non-empty array of records." });

        foreach (var r in records)
            if (string.IsNullOrWhiteSpace(r.UniqueKey))
                r.UniqueKey = UniqueKeyBuilder.JobReport(r.DealerCode, r.JobNo, r.JobDate, r.ChassisNo);

        var deduped = records.GroupBy(r => r.UniqueKey).Select(g => g.Last()).ToList();
        var keys = deduped.Select(r => r.UniqueKey!).ToList();

        var existing = await _db.DmsJobReports
            .Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
            .ToListAsync();
        var lookup = existing.ToDictionary(x => x.UniqueKey!, x => x);

        int inserted = 0, updated = 0;

        foreach (var r in deduped)
        {
            if (lookup.TryGetValue(r.UniqueKey!, out var row))
            {
                ApplyJobReport(row, r);
                row.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.DmsJobReports.Add(MapJobReport(r));
                inserted++;
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("JobReport bulk upsert: {ins} inserted, {upd} updated", inserted, updated);
        return Ok(new { Inserted = inserted, Updated = updated });
    }

    private static DmsJobReport MapJobReport(JobReportRecordDto r) => new()
    {
        DealerName = r.DealerName, DealerCode = r.DealerCode, DealerLocation = r.DealerLocation,
        City = r.City, State = r.State, JobNo = r.JobNo, JobDate = r.JobDate, JobType = r.JobType,
        ServiceHead = r.ServiceHead, ServiceType = r.ServiceType, Kms = r.Kms, CustomerName = r.CustomerName,
        MobileNo = r.MobileNo, ChassisNo = r.ChassisNo, RegNo = r.RegNo, EngineNo = r.EngineNo,
        ItemName = r.ItemName, CustomerVoice = r.CustomerVoice, ComplaintCode = r.ComplaintCode,
        Observation = r.Observation, SupervisorComment = r.SupervisorComment, JobStatus = r.JobStatus,
        BatteryNo = r.BatteryNo, BrandName = r.BrandName, ChargerNo = r.ChargerNo, SaleDate = r.SaleDate,
        Supervisor = r.Supervisor, Technician = r.Technician, JobEndDate = r.JobEndDate,
        CreatedThrough = r.CreatedThrough, UniqueKey = r.UniqueKey,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static void ApplyJobReport(DmsJobReport row, JobReportRecordDto r)
    {
        row.DealerName = r.DealerName; row.DealerCode = r.DealerCode; row.DealerLocation = r.DealerLocation;
        row.City = r.City; row.State = r.State; row.JobNo = r.JobNo; row.JobDate = r.JobDate; row.JobType = r.JobType;
        row.ServiceHead = r.ServiceHead; row.ServiceType = r.ServiceType; row.Kms = r.Kms; row.CustomerName = r.CustomerName;
        row.MobileNo = r.MobileNo; row.ChassisNo = r.ChassisNo; row.RegNo = r.RegNo; row.EngineNo = r.EngineNo;
        row.ItemName = r.ItemName; row.CustomerVoice = r.CustomerVoice; row.ComplaintCode = r.ComplaintCode;
        row.Observation = r.Observation; row.SupervisorComment = r.SupervisorComment; row.JobStatus = r.JobStatus;
        row.BatteryNo = r.BatteryNo; row.BrandName = r.BrandName; row.ChargerNo = r.ChargerNo; row.SaleDate = r.SaleDate;
        row.Supervisor = r.Supervisor; row.Technician = r.Technician; row.JobEndDate = r.JobEndDate;
        row.CreatedThrough = r.CreatedThrough;
    }
}