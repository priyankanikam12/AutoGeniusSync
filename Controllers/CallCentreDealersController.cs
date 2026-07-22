using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoGeniusSync.DTOs;

namespace AutoGeniusSync.Controllers;
// ─────────────────────────────────────────────────────────────
// Call Centre Dealers Controller
// ─────────────────────────────────────────────────────────────
[ApiController]
[Route("api/[controller]")]
public class CallCentreDealersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DataSyncService _sync;

    public CallCentreDealersController(AppDbContext db, DataSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    // GET /api/callcentredealers
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? state,
        [FromQuery] string? activeStatus)
    {
        var q = _db.DmsCallCentreDealers.AsQueryable();

        if (!string.IsNullOrEmpty(state))
            q = q.Where(d => d.DealerStateName != null && d.DealerStateName.Contains(state));

        if (!string.IsNullOrEmpty(activeStatus))
            q = q.Where(d => d.ActiveStatus == activeStatus);

        return Ok(await q.OrderBy(d => d.DealerCompany).ToListAsync());
    }

    // GET /api/callcentredealers/pincode/401105
    [HttpGet("pincode/{pin}")]
    public async Task<IActionResult> GetByPin(string pin)
        => Ok(await _db.DmsCallCentreDealers
            .Where(d => d.PinCode == pin)
            .ToListAsync());

    // GET /api/callcentredealers/{dealerCode}
    [HttpGet("{dealerCode}")]
    public async Task<IActionResult> Get(string dealerCode)
    {
        var d = await _db.DmsCallCentreDealers
            .FirstOrDefaultAsync(x => x.DealerCode == dealerCode);
        return d == null ? NotFound() : Ok(d);
    }

    // POST /api/callcentredealers/sync
    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
        => Ok(await _sync.SyncCallCentreDealersAsync());
}