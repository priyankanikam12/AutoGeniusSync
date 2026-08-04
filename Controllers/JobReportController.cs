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

    // ─────────────────────────────────────────────────────
    // POST /api/jobreport/upload — INSERT ONLY, skips existing rows
    // ─────────────────────────────────────────────────────
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

    // ─────────────────────────────────────────────────────
    // PUT /api/jobreport/upload — UPSERT: insert new, update existing
    // ─────────────────────────────────────────────────────
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

    // GET /api/jobreport — quick listing/filter for verification
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
}