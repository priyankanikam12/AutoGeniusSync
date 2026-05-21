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
}
