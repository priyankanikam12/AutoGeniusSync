using AutoGeniusSync.Data;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DealersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<DealersController> _logger;

    public DealersController(AppDbContext db, ILogger<DealersController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? state, [FromQuery] string? city)
    {
        var q = _db.DmsDealers.AsQueryable();
        if (!string.IsNullOrEmpty(state)) q = q.Where(d => d.DealerStateName != null && d.DealerStateName.Contains(state));
        if (!string.IsNullOrEmpty(city))  q = q.Where(d => d.DealerCityName  != null && d.DealerCityName.Contains(city));
        return Ok(await q.OrderBy(d => d.DealerCompany).ToListAsync());
    }

    [HttpGet("{dealerCode}")]
    public async Task<IActionResult> Get(string dealerCode)
    {
        var d = await _db.DmsDealers.FirstOrDefaultAsync(x => x.DealerCode == dealerCode);
        return d == null ? NotFound() : Ok(d);
    }

    [HttpGet("pincode/{pin}")]
    public async Task<IActionResult> GetByPin(string pin)
        => Ok(await _db.DmsDealers.Where(d => d.PinCode == pin).ToListAsync());

    // ──────────────────────────────────────────────────────
    // POST /api/dealers/upload-master
    //
    // WHY THIS EXISTS: pincode-driven discovery (SyncAllDealersAsync) only
    // finds dealers whose registered pincode is in SyncSettings:Pincodes,
    // and its State/City come from the pincode API response — which can
    // drift from the ERP's own authoritative dealer record. This endpoint
    // loads the ERP's own ManageDealerReport export directly, keyed by
    // DealerCode, and treats it as the source of truth for
    // State/City/Status/Email — overwriting whatever pincode sync had.
    //
    // Expected columns (header row required), in this order:
    // Company Code,Company Name,Dealer Code,Email Id,MobileNo,State,City,
    // City Tier,Source,Current Status,Registration Date,Modified By,
    // Previous Status,Modified Date,Remarks
    // ──────────────────────────────────────────────────────
    [HttpPost("upload-master")]
    public async Task<IActionResult> UploadMaster(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        int inserted = 0, updated = 0, skipped = 0;
        var errors = new List<string>();

        using var reader = new StreamReader(file.OpenReadStream());
        var header = await reader.ReadLineAsync(); // skip header row

        int lineNo = 1;
        while (!reader.EndOfStream)
        {
            lineNo++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitCsvLine(line);
            if (cols.Length < 10) { skipped++; errors.Add($"Line {lineNo}: too few columns"); continue; }

            var dealerCode = cols[2].Trim(); // "Dealer Code" column
            if (string.IsNullOrEmpty(dealerCode)) { skipped++; continue; }

            var companyName = cols[1].Trim();
            var email       = cols[3].Trim();
            var mobileNo    = cols[4].Trim();
            var state       = cols[5].Trim();
            var city        = cols[6].Trim();
            var cityTier    = cols.Length > 7 ? cols[7].Trim() : null;
            var source      = cols.Length > 8 ? cols[8].Trim() : null;
            var status      = cols.Length > 9 ? cols[9].Trim() : null;
            var regDate     = cols.Length > 10 ? ParseDate(cols[10]) : null;
            var modifiedBy  = cols.Length > 11 ? cols[11].Trim() : null;
            var prevStatus  = cols.Length > 12 ? cols[12].Trim() : null;
            var modDate     = cols.Length > 13 ? ParseDate(cols[13]) : null;
            var remarks     = cols.Length > 14 ? cols[14].Trim() : null;

            try
            {
                var existing = await _db.DmsDealers
                    .FirstOrDefaultAsync(x => x.DealerCode == dealerCode);

                if (existing == null)
                {
                    _db.DmsDealers.Add(new DmsDealer
                    {
                        DealerCode         = dealerCode,
                        DealerCompany      = companyName,
                        ContactNo          = mobileNo,
                        DealerStateName    = state,
                        DealerCityName     = city,
                        ActiveStatus       = status,
                        Email              = email,
                        CityTier           = cityTier,
                        Source             = source,
                        RegistrationDate   = regDate,
                        ModifiedBy         = string.IsNullOrEmpty(modifiedBy) ? null : modifiedBy,
                        PreviousStatus     = string.IsNullOrEmpty(prevStatus) ? null : prevStatus,
                        StatusModifiedDate = modDate,
                        Remarks            = string.IsNullOrEmpty(remarks) ? null : remarks,
                        LastFetchedAt      = DateTime.UtcNow,
                        CreatedAt          = DateTime.UtcNow,
                        UpdatedAt          = DateTime.UtcNow
                    });
                    inserted++;
                }
                else
                {
                    // Authoritative overwrite — this report is the source of truth
                    // for state/city/status, taking precedence over pincode sync.
                    existing.DealerCompany      = companyName;
                    existing.ContactNo          = mobileNo;
                    existing.DealerStateName    = state;
                    existing.DealerCityName     = city;
                    existing.ActiveStatus       = status;
                    existing.Email              = email;
                    existing.CityTier           = cityTier;
                    existing.Source             = source;
                    existing.RegistrationDate   = regDate;
                    existing.ModifiedBy         = string.IsNullOrEmpty(modifiedBy) ? existing.ModifiedBy : modifiedBy;
                    existing.PreviousStatus     = string.IsNullOrEmpty(prevStatus) ? existing.PreviousStatus : prevStatus;
                    existing.StatusModifiedDate = modDate ?? existing.StatusModifiedDate;
                    existing.Remarks            = string.IsNullOrEmpty(remarks) ? existing.Remarks : remarks;
                    existing.LastFetchedAt      = DateTime.UtcNow;
                    existing.UpdatedAt          = DateTime.UtcNow;
                    updated++;
                }
            }
            catch (Exception ex)
            {
                skipped++;
                errors.Add($"Line {lineNo} ({dealerCode}): {ex.Message}");
                _logger.LogWarning("Dealer master upload: skipped {code} at line {line}: {msg}",
                    dealerCode, lineNo, ex.Message);
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Dealer master upload: {ins} inserted, {upd} updated, {skip} skipped",
            inserted, updated, skipped);

        return Ok(new
        {
            Inserted = inserted,
            Updated  = updated,
            Skipped  = skipped,
            Errors   = errors.Take(20) // cap so response isn't huge
        });
    }

    // Splits a CSV line respecting quoted fields (handles commas inside
    // quotes, e.g. remarks that might contain commas).
    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        result.Add(current.ToString());
        return result.ToArray();
    }

    private static DateOnly? ParseDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        if (DateTime.TryParseExact(val.Trim(), "dd-MM-yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d))
            return DateOnly.FromDateTime(d);
        return null;
    }
}