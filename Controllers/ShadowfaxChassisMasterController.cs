// Controllers/ShadowfaxChassisMasterController.cs
using AutoGeniusSync.Data;
using AutoGeniusSync.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Controllers;

[ApiController]
[Route("api/shadowfax/chassis-master")]
public class ShadowfaxChassisMasterController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<ShadowfaxChassisMasterController> _logger;

    public ShadowfaxChassisMasterController(AppDbContext db, ILogger<ShadowfaxChassisMasterController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // POST /api/shadowfax/chassis-master/upload
    // Accepts the CSV Shadowfax sends (Vehicle_ID,Model,Registration_number,
    // Chassis_number,City,Vehicle_Current_Status) and upserts by ChassisNo.
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        int inserted = 0, updated = 0, skipped = 0;

        using var reader = new StreamReader(file.OpenReadStream());
        var header = await reader.ReadLineAsync(); // skip header row

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');
            if (cols.Length < 6) { skipped++; continue; }

            var chassisNo = cols[3].Trim();
            if (string.IsNullOrEmpty(chassisNo)) { skipped++; continue; }

            var existing = await _db.DmsShadowfaxChassisMasters
                .FirstOrDefaultAsync(x => x.ChassisNo == chassisNo);

            if (existing == null)
            {
                _db.DmsShadowfaxChassisMasters.Add(new DmsShadowfaxChassisMaster
                {
                    VehicleId          = cols[0].Trim(),
                    Model              = cols[1].Trim(),
                    RegistrationNumber = cols[2].Trim(),
                    ChassisNo          = chassisNo,
                    City               = cols[4].Trim(),
                    VehicleStatus      = cols[5].Trim(),
                    CreatedAt          = DateTime.UtcNow,
                    UpdatedAt          = DateTime.UtcNow
                });
                inserted++;
            }
            else
            {
                existing.VehicleId          = cols[0].Trim();
                existing.Model              = cols[1].Trim();
                existing.RegistrationNumber = cols[2].Trim();
                existing.City               = cols[4].Trim();
                existing.VehicleStatus      = cols[5].Trim();
                existing.UpdatedAt          = DateTime.UtcNow;
                updated++;
            }
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Shadowfax chassis master upload: {ins} inserted, {upd} updated, {skip} skipped",
            inserted, updated, skipped);

        return Ok(new { Inserted = inserted, Updated = updated, Skipped = skipped });
    }

    // GET /api/shadowfax/chassis-master/count
    [HttpGet("count")]
    public async Task<IActionResult> Count()
        => Ok(new { Total = await _db.DmsShadowfaxChassisMasters.CountAsync() });
}