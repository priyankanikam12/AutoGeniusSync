using AutoGeniusSync.Data;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;

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

        IWorkbook workbook;
        try
        {
            // Copy into a MemoryStream first — NPOI needs a seekable stream, and
            // IFormFile's underlying stream isn't guaranteed to support seeking.
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;
            workbook = WorkbookFactory.Create(ms); // auto-detects .xls (HSSF) vs .xlsx (XSSF)
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Could not read the file as an Excel workbook: {ex.Message}" });
        }

        var sheet = workbook.GetSheetAt(0);
        if (sheet == null || sheet.LastRowNum < 1)
            return BadRequest(new { error = "Sheet is empty or has no data rows." });

        // Row 0 is the header — skip it, same as the old header = ReadLineAsync() did.
        for (int rowIndex = 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row == null) continue; // NPOI can return null for fully blank rows

            // LastCellNum is 1-based count of the highest cell index + 1; guard the
            // same "too few columns" check the original code had.
            if (row.LastCellNum < 10)
            {
                skipped++;
                errors.Add($"Row {rowIndex + 1}: too few columns");
                continue;
            }

            string Cell(int idx) => GetCellString(row, idx);

            var dealerCode = Cell(2).Trim(); // "Dealer Code" column
            if (string.IsNullOrEmpty(dealerCode)) { skipped++; continue; }

            var companyName = Cell(1).Trim();
            var email       = Cell(3).Trim();
            var mobileNo    = Cell(4).Trim();
            var state       = Cell(5).Trim();
            var city        = Cell(6).Trim();
            var cityTier    = row.LastCellNum > 7  ? Cell(7).Trim()  : null;
            var source      = row.LastCellNum > 8  ? Cell(8).Trim()  : null;
            var status      = row.LastCellNum > 9  ? Cell(9).Trim()  : null;
            var regDate     = row.LastCellNum > 10 ? ParseDate(Cell(10)) : null;
            var modifiedBy  = row.LastCellNum > 11 ? Cell(11).Trim() : null;
            var prevStatus  = row.LastCellNum > 12 ? Cell(12).Trim() : null;
            var modDate     = row.LastCellNum > 13 ? ParseDate(Cell(13)) : null;
            var remarks     = row.LastCellNum > 14 ? Cell(14).Trim() : null;

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
                errors.Add($"Row {rowIndex + 1} ({dealerCode}): {ex.Message}");
                _logger.LogWarning("Dealer master upload: skipped {code} at row {row}: {msg}",
                    dealerCode, rowIndex + 1, ex.Message);
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

    // Reads any cell type (string, numeric, date, boolean, formula, blank) as a
    // trimmed string. Excel date cells come back as DateTime — normalized to
    // "dd-MM-yyyy" so it matches the text-date format your ParseDate(string)
    // helper already expects (e.g. "19-01-2022"), regardless of whether the
    // source cell was a real Excel date or plain text.
    private static string GetCellString(IRow row, int index)
    {
        var cell = row.GetCell(index);
        if (cell == null) return string.Empty;

        switch (cell.CellType)
        {
            case CellType.Blank:
                return string.Empty;
            case CellType.String:
                return cell.StringCellValue?.Trim() ?? string.Empty;
            case CellType.Numeric:
                if (DateUtil.IsCellDateFormatted(cell))
                    return cell.DateCellValue?.ToString("dd-MM-yyyy") ?? string.Empty;
                return cell.NumericCellValue.ToString();
            case CellType.Boolean:
                return cell.BooleanCellValue.ToString();
            case CellType.Formula:
                // Evaluate common formula result types rather than returning the formula text.
                return cell.CachedFormulaResultType switch
                {
                    CellType.String => cell.StringCellValue?.Trim() ?? string.Empty,
                    CellType.Numeric => cell.NumericCellValue.ToString(),
                    _ => cell.ToString() ?? string.Empty
                };
            default:
                return cell.ToString() ?? string.Empty;
        }
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