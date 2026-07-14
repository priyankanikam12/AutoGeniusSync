using AutoGeniusSync.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DealersController : ControllerBase
{
    private readonly AppDbContext _db;
    public DealersController(AppDbContext db) => _db = db;

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

    // ── NEW: GET /api/dealers/search?name=shadowfax ──────────
    // Search dealer company names by substring — use this to
    // find the exact DealerCode(s) for Shadowfax-onboarded dealers
    // so they can be added to ShadowfaxSettings:DealerCodes in
    // appsettings.json. Try "shadowfax" first; if nothing matches,
    // your ERP may have them under a different partner/tag name —
    // ask your ERP admin what dealer company name is used internally
    // for the Shadowfax hub-partner dealers.
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
}