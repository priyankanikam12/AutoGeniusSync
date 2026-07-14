using AutoGeniusSync.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DealersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public DealersController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
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

    // ── GET /api/dealers/search?name=shadowfax ────────────────
    // Search dealer company names by substring — use this to
    // find the exact DealerCode(s) for Shadowfax-onboarded dealers
    // so they can be added to ShadowfaxSettings:DealerCodes in
    // appsettings.json.
    [HttpGet("search")]
    public async Task<IActionResult> SearchByName([FromQuery] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Provide ?name=... to search dealer company names." });

        var results = await _db.DmsDealers
            .Where(d => d.DealerCompany != null && d.DealerCompany.Contains(name))
            .Select(d => new { d.DealerCode, d.DealerCompany, d.DealerCityName, d.DealerStateName, d.PinCode, d.ActiveStatus })
            .OrderBy(d => d.DealerCompany)
            .ToListAsync();

        return Ok(new { Count = results.Count, Results = results });
    }

    // ── NEW: GET /api/dealers/shadowfax-status ─────────────────
    // Cross-checks ShadowfaxSettings:DealerCodes (appsettings.json)
    // against what's actually present in DMS_Dealers, so you can see
    // at a glance which configured Shadowfax dealer codes are still
    // missing locally. LOR/VSR sync will still pull data for them
    // regardless (see DataSyncService.GetConfiguredShadowfaxDealerCodes),
    // but they won't show up in GET /api/dealers or /api/dealers/{code}
    // until a regular dealer sync happens to return them via pincode,
    // or you seed stub rows manually.
    [HttpGet("shadowfax-status")]
    public async Task<IActionResult> ShadowfaxStatus()
    {
        var configured = _config.GetSection("ShadowfaxSettings:DealerCodes").Get<List<string>>() ?? new();

        var existing = await _db.DmsDealers
            .Where(d => d.DealerCode != null && configured.Contains(d.DealerCode))
            .Select(d => d.DealerCode!)
            .ToListAsync();

        var missing = configured
            .Except(existing, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        return Ok(new
        {
            ConfiguredCount   = configured.Count,
            ExistingInDbCount = existing.Count,
            MissingCount      = missing.Count,
            MissingDealerCodes = missing
        });
    }
}