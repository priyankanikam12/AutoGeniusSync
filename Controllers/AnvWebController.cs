using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AutoGeniusSync.Models;

namespace AutoGeniusSync.Controllers
{
    // Read-only partner API for ANV Web Ventures Private Limited's own dashboard.
    // Everything here is GET — this exists purely to GIVE them data pulled from our
    // own database (DMS_VehicleSales, DMS_VehicleDispatches, DMS_ServiceHistory);
    // it has no insert/update/delete endpoints, and nothing here accepts a payload.
    //
    // Every endpoint is hard-scoped server-side to SoldTo LIKE '%ANV Web Ventures%'
    // (DMS_VehicleSales is the source of truth for which chassis numbers belong to
    // them) — the caller cannot widen this with any parameter — and gated by header
    // "X-API-Key" against appsettings "ApiKeys:ANVWeb" (the same key already used
    // for the ServiceHistory/LineOrderReport/VehicleSales ANVWeb endpoints).
    [ApiController]
    [Route("api/anvweb")]
    public class AnvWebController : ControllerBase
    {
        private const string PartyMatch = "ANV Web Ventures";

        private readonly AppDbContext _db;
        private readonly IConfiguration _config;

        public AnvWebController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        private bool IsValidApiKey(string? apiKey)
        {
            var expected = _config["ApiKeys:ANVWeb"];
            return !string.IsNullOrEmpty(expected) && string.Equals(apiKey, expected, StringComparison.Ordinal);
        }

        // GET api/anvweb/fleet-status?chassisNo=...&page=1&pageSize=100
        // One row per vehicle sold to ANV Web Ventures: ownership + vehicle master
        // fields + their current repair/service status (from the most recent
        // service-history job for that chassis, if any).
        [HttpGet("fleet-status")]
        public async Task<IActionResult> GetFleetStatus(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromQuery] string? chassisNo,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 100;

            var saleQuery = _db.DmsVehicleSales
                .Where(v => v.SoldTo != null && v.SoldTo.Contains(PartyMatch));

            if (!string.IsNullOrEmpty(chassisNo))
                saleQuery = saleQuery.Where(v => v.ChassisNo != null && v.ChassisNo.Contains(chassisNo));

            var total = await saleQuery.CountAsync();

            var pageSales = await saleQuery
                .OrderByDescending(v => v.InvoiceDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var chassisList = pageSales
                .Select(s => s.ChassisNo)
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c!)
                .Distinct()
                .ToList();

            var dispatches = await _db.DmsVehicleDispatches
                .Where(d => d.ChassisNo != null && chassisList.Contains(d.ChassisNo))
                .ToListAsync();
            var dispatchLookup = dispatches
                .GroupBy(d => d.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First());

            var serviceRows = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo != null && chassisList.Contains(s.ChassisNo))
                .OrderByDescending(s => s.JobDate)
                .ToListAsync();
            var serviceLookup = serviceRows
                .GroupBy(s => s.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.ToList()); // already ordered desc within each group

            var records = pageSales.Select(s =>
            {
                DmsVehicleDispatch? dispatch = null;
                List<DmsServiceHistory>? serviceJobs = null;

                if (!string.IsNullOrEmpty(s.ChassisNo))
                {
                    dispatchLookup.TryGetValue(s.ChassisNo, out dispatch);
                    serviceLookup.TryGetValue(s.ChassisNo, out serviceJobs);
                }

                var latestJob = serviceJobs?.FirstOrDefault();

                return new AnvWebFleetStatusDto
                {
                    ChassisNo = s.ChassisNo,
                    RegistrationNo = dispatch?.RegNo ?? dispatch?.RegNumber,
                    // Prefer the friendly model name on the sale/job record — DmsVehicleDispatch.BrandName
                    // actually holds a vehicle-group code (e.g. "FG"/"FGWG"), not a real model name.
                    Model = latestJob?.Model ?? s.ItemModel ?? s.Oemmodel,
                    Location = dispatch?.Location ?? s.Location,
                    SoldTo = s.SoldTo,
                    SaleDate = s.InvoiceDate,
                    VehicleStatus = dispatch?.VehicleStatus,
                    TotalServiceJobs = serviceJobs?.Count ?? 0,
                    LatestJobNo = latestJob?.JobNo,
                    LatestJobDate = latestJob?.JobDate,
                    LatestJobType = latestJob?.JobType,
                    LatestJobStatus = latestJob?.JobStatus,
                    LatestCompletionDate = latestJob?.CompletionDate,
                    LatestRepairType = latestJob?.RepairType
                };
            }).ToList();

            return Ok(new { Total = total, Page = page, PageSize = pageSize, Records = records });
        }

        // GET api/anvweb/fleet-status/summary
        // Dashboard stat-tile data: total vehicles, how many currently have an open
        // service job vs. none, and a breakdown by location.
        [HttpGet("fleet-status/summary")]
        public async Task<IActionResult> GetFleetStatusSummary([FromHeader(Name = "X-API-Key")] string? apiKey)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            var chassisList = await _db.DmsVehicleSales
                .Where(v => v.SoldTo != null && v.SoldTo.Contains(PartyMatch) && v.ChassisNo != null)
                .Select(v => v.ChassisNo!)
                .Distinct()
                .ToListAsync();

            var totalVehicles = chassisList.Count;

            // A vehicle is "in service" if its most recent service-history job has no
            // CompletionDate/InvoiceDate yet (mirrors how DmsServiceHistory.JobStatus
            // is computed as Open/Closed).
            var openChassis = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo != null && chassisList.Contains(s.ChassisNo)
                            && s.JobStatus != "Closed")
                .Select(s => s.ChassisNo!)
                .Distinct()
                .ToListAsync();

            var byLocation = await _db.DmsVehicleSales
                .Where(v => v.SoldTo != null && v.SoldTo.Contains(PartyMatch))
                .GroupBy(v => v.Location)
                .Select(g => new { Location = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            return Ok(new
            {
                TotalVehicles = totalVehicles,
                InService = openChassis.Count,
                Available = totalVehicles - openChassis.Count,
                ByLocation = byLocation
            });
        }

        // GET api/anvweb/fleet-status/model-breakdown
        // EV-model x maintenance-type grid: for each model, how many of ANV Web
        // Ventures' vehicles are currently off-road under an open service-history
        // job, split into Repair / Service / Insurance, and further split by how
        // many days that job has been open (0-3, 4-7, 8-15, 16-30, 30+).
        //
        // IMPORTANT CAVEAT: DMS_ServiceHistory has no dedicated "insurance claim"
        // flag. Repair vs Service vs Insurance is inferred here from RepairType /
        // JobType / JobCategory text containing "insur" / "repair" (anything else
        // open falls into Service, since that's this table's default purpose).
        // If your JobCategory values don't actually distinguish insurance jobs,
        // the Insurance column below will read 0 for everyone — that's a data
        // gap, not a bug in the query. Tell me the real values in JobCategory /
        // RepairType and I'll tighten the classification.
        [HttpGet("fleet-status/model-breakdown")]
        public async Task<IActionResult> GetModelBreakdown([FromHeader(Name = "X-API-Key")] string? apiKey)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            // Chassis -> Model, for every vehicle sold to ANV Web Ventures.
            var sales = await _db.DmsVehicleSales
                .Where(v => v.SoldTo != null && v.SoldTo.Contains(PartyMatch) && v.ChassisNo != null)
                .Select(v => new { v.ChassisNo, v.ItemModel, v.Oemmodel })
                .ToListAsync();

            var chassisList = sales.Select(s => s.ChassisNo!).Distinct().ToList();
            if (chassisList.Count == 0)
                return Ok(new { Models = Array.Empty<ModelMaintenanceBreakdownDto>() });

            // Fallback model name if a chassis's open job somehow has no Model set.
            // NOTE: DmsVehicleDispatch.BrandName is NOT a friendly model name in this
            // schema — it holds a vehicle-group code (e.g. "FG"/"FGWG") — so it is
            // deliberately not used as a model source anywhere in this endpoint.
            var saleModelByChassis = sales
                .GroupBy(s => s.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First().ItemModel ?? g.First().Oemmodel);

            // Only currently-open jobs count as "off-road" for this grid.
            var openJobs = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo != null && chassisList.Contains(s.ChassisNo)
                            && s.JobStatus != "Closed")
                .OrderByDescending(s => s.JobDate)
                .ToListAsync();

            // One open job per chassis — the most recent.
            var latestOpenJobByChassis = openJobs
                .GroupBy(s => s.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First());

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var byModel = new Dictionary<string, ModelMaintenanceBreakdownDto>();

            foreach (var chassis in chassisList)
            {
                if (!latestOpenJobByChassis.TryGetValue(chassis, out var job) || job.JobDate == null)
                    continue; // not currently off-road — excluded from this grid, same as the RIDEV report

                // Prefer the friendly name already on the job itself (DmsServiceHistory.Model,
                // e.g. "BGauss B8 Lithium Nebula Grey WG"); fall back to the sale record.
                var model = job.Model
                    ?? (saleModelByChassis.TryGetValue(chassis, out var m) ? m : null)
                    ?? "Unknown";

                if (!byModel.TryGetValue(model, out var row))
                {
                    row = new ModelMaintenanceBreakdownDto { Model = model };
                    byModel[model] = row;
                }

                var days = today.DayNumber - job.JobDate.Value.DayNumber;
                if (days < 0) days = 0;

                var classifyText = $"{job.RepairType} {job.JobType} {job.JobCategory}".ToLowerInvariant();

                if (classifyText.Contains("insur"))
                    row.Insurance.Add(days);
                else if (classifyText.Contains("repair"))
                    row.Repair.Add(days);
                else
                    row.Service.Add(days);
            }

            var result = byModel.Values.OrderByDescending(m => m.Repair.Total + m.Service.Total + m.Insurance.Total).ToList();
            return Ok(new { Models = result });
        }
    }
}
