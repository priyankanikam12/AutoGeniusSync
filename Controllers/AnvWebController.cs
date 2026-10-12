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

        // GET api/anvweb/lookup?chassisNo=...   (or ?registrationNo=...)
        //
        // Added in response to Balaji's (ANV Web Ventures) feedback that the three
        // fleet-status endpoints above don't meet their requirement: they want to
        // GET a single vehicle's data by searching on chassis number (registration
        // number also supported), returning "the required fields" — i.e. the
        // columns on their RIDEV dashboard (Bike Name, Location, Status, Chassis
        // Number, Last Subscription Date, Date of Reporting, Actual/Expected Date
        // of RFD, Days Delayed, Reason).
        //
        // READ THIS BEFORE TELLING BALAJI THIS IS DONE: AutoGeniusSync's schema does
        // not contain most of those fields — there is no current_status enum
        // (repair/service_center/insurance/un_allocated), no RFD target dates, no
        // days-delayed calculation, and no subscription dates anywhere in this
        // database, including in DmsLineOrderReport (checked below too — it's a
        // job-card/invoice feed with RegNo/Model/Location/JobDate/JobCardType, not
        // a status-tracking table). That data lives only in the separate RIDEV
        // (admin.ridev.in) system, which this codebase has no access to. This
        // endpoint returns the closest equivalents AutoGeniusSync actually has
        // (see AnvWebLookupResponseDto for the exact mapping, and DmsLineOrderReport
        // used below as an extra fallback source) and explicitly lists which
        // requested fields it cannot supply in UnavailableFields/Note on every
        // response, so this can be shared with Balaji/Priyanka as-is without
        // overstating what it covers.
        [HttpGet("lookup")]
        public async Task<IActionResult> Lookup(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromQuery] string? chassisNo,
            [FromQuery] string? registrationNo)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (string.IsNullOrWhiteSpace(chassisNo) && string.IsNullOrWhiteSpace(registrationNo))
                return BadRequest(new { error = "Provide chassisNo or registrationNo as a query parameter." });

            string? chassis = string.IsNullOrWhiteSpace(chassisNo) ? null : chassisNo.Trim();

            // Resolve registrationNo -> chassis when chassisNo wasn't given. RegNo
            // lives directly on DmsLineOrderReport (per job card) as well as on
            // DmsVehicleDispatch — check the dispatch master first (it's one row
            // per vehicle), then fall back to the LOR job-card table, since a
            // vehicle can have LOR rows even when it has no dispatch row yet.
            if (chassis == null && !string.IsNullOrWhiteSpace(registrationNo))
            {
                var regNo = registrationNo.Trim();
                var dispatchMatch = await _db.DmsVehicleDispatches
                    .Where(d => d.ChassisNo != null &&
                                ((d.RegNo != null && d.RegNo == regNo) ||
                                 (d.RegNumber != null && d.RegNumber == regNo)))
                    .FirstOrDefaultAsync();

                chassis = dispatchMatch?.ChassisNo;

                if (chassis == null)
                {
                    var lorMatch = await _db.DmsLineOrderReports
                        .Where(l => l.ChassisNo != null && l.RegNo == regNo)
                        .FirstOrDefaultAsync();
                    chassis = lorMatch?.ChassisNo;
                }

                if (chassis == null)
                    return NotFound(new { error = $"No vehicle found for registration number '{regNo}'." });
            }

            // Hard party scope, same as every other endpoint here — a caller can't
            // use this to look up a chassis that isn't ANV Web Ventures' own.
            var sale = await _db.DmsVehicleSales
                .Where(v => v.ChassisNo == chassis && v.SoldTo != null && v.SoldTo.Contains(PartyMatch))
                .FirstOrDefaultAsync();

            if (sale == null)
                return NotFound(new { error = $"No ANV Web Ventures vehicle found for chassis number '{chassis}'." });

            var dispatch = await _db.DmsVehicleDispatches
                .Where(d => d.ChassisNo == chassis)
                .FirstOrDefaultAsync();

            var latestJob = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo == chassis)
                .OrderByDescending(s => s.JobDate)
                .FirstOrDefaultAsync();

            // DmsLineOrderReport (LOR) is the job-card/invoice feed — it carries RegNo
            // directly (no dispatch join needed) plus its own Model/Location/JobDate/
            // JobCardType per job. Used here as a fallback wherever DmsServiceHistory
            // or DmsVehicleDispatch don't have a value, and as a second date/reason
            // candidate. NOTE: assumes the registered DbSet is "DmsLineOrderReports" —
            // rename if your DbContext registers it differently.
            var latestLor = await _db.DmsLineOrderReports
                .Where(l => l.ChassisNo == chassis)
                .OrderByDescending(l => l.JobDate ?? l.DocDate)
                .FirstOrDefaultAsync();

            var status = latestJob == null
                ? "No service history"
                : (latestJob.JobStatus == "Closed" ? "Closed" : "Open");

            var response = new AnvWebLookupResponseDto
            {
                ChassisNumber = chassis,
                RegistrationNo = dispatch?.RegNo ?? dispatch?.RegNumber ?? latestLor?.RegNo,
                BikeName = latestJob?.Model ?? latestLor?.Model ?? sale.ItemModel ?? sale.Oemmodel,
                Location = dispatch?.Location ?? latestLor?.Location ?? sale.Location,
                Status = status,
                LastSubscriptionDate = null,       // not tracked anywhere in AutoGeniusSync
                DateOfReporting = latestJob?.JobDate ?? latestLor?.JobDate ?? latestLor?.DocDate,
                ActualDateOfRfd = latestJob?.CompletionDate,
                ExpectedDateOfRfd = null,           // not tracked anywhere in AutoGeniusSync
                DaysDelayed = null,                 // not tracked anywhere in AutoGeniusSync
                Reason = latestJob?.RepairType ?? latestJob?.JobType ?? latestLor?.JobCardType ?? latestLor?.DocType,
                UnavailableFields = new[] { "lastSubscriptionDate", "expectedDateOfRfd", "daysDelayed" },
                Note = "AutoGeniusSync does not track subscription dates, expected RFD dates, or a " +
                       "days-delayed calculation — those fields are always null here. 'status' is only " +
                       "Open/Closed/No service history (not the repair/service_center/insurance/" +
                       "un_allocated categorization on your dashboard). 'actualDateOfRfd' is the service " +
                       "job's completion date and 'reason' is its repair/job type — the closest " +
                       "equivalents this system has, not the same underlying data."
            };

            return Ok(response);
        }

        // ---------------------------------------------------------------------
        // "maintenance" endpoints — built to the exact JSON contract the partner's
        // integration team specified (their email: registration_number/
        // chassis_number match keys, maintenance_type, expected_rfd, vehicle_at_hub,
        // hub_location, partner_remark, assessment_status, final_status,
        // parts_required[], invoices[], updated_at, with updated_since + pagination
        // on the list endpoint).
        //
        // YOU CHOSE to ship this now with only what AutoGeniusSync actually has
        // rather than building a new hub-assessment table first. That means five
        // of their fields are NOT real data:
        //   - expected_rfd, vehicle_at_hub, partner_remark, assessment_status:
        //     nothing in this schema tracks a physical hub check-in, an RFD
        //     target, a free-text remark, or an explicit assessed/pending flag.
        //     assessment_status is a proxy (see the AssessmentStatus assignment in
        //     BuildMaintenanceRecords below); the other three are always null.
        //   - maintenance_type / final_status: inferred from job-type text and
        //     open/closed status, not a real workflow field.
        // Every response carries a `_meta` block spelling this out — don't strip
        // it before sharing sample payloads, since it's the only thing standing
        // between "here's our API" and re-litigating the same gap Balaji already
        // flagged once on the fleet-status endpoints.
        // ---------------------------------------------------------------------

        private static readonly string[] MaintenanceUnavailableFields =
            { "expected_rfd", "vehicle_at_hub", "partner_remark", "assessment_status" };

        private const string MaintenanceGapNote =
            "expected_rfd, vehicle_at_hub, and partner_remark are not tracked anywhere in " +
            "AutoGeniusSync and are always null. assessment_status is a proxy (\"assessed\" if " +
            "we've raised a Proforma estimate for the vehicle, otherwise \"pending\") rather than " +
            "a real assessment flag. maintenance_type and final_status are inferred from job-type " +
            "text and open/closed job status respectively, not first-class workflow fields. " +
            "hub_location is our on-file dealer/service location, not a hub-network concept. " +
            "parts_required.insurance_covered has no reliable source and is always null; " +
            "warranty_covered is inferred from the line item's IssueType == \"U/W\". " +
            "invoices[].file_url is always null — this system stores no documents.";

        private static string ClassifyMaintenanceType(string? repairType, string? jobType, string? jobCategory)
        {
            var text = $"{repairType} {jobType} {jobCategory}".ToLowerInvariant();
            if (text.Contains("insur")) return "insurance";
            if (text.Contains("running")) return "running_repair";
            if (text.Contains("repair")) return "repair";
            return "service";
        }

        // GET api/anvweb/maintenance?updated_since=2026-09-01&page=1&pageSize=100
        // One row per ANV Web Ventures vehicle currently under maintenance (an open,
        // non-Closed DmsServiceHistory job). updated_since filters on the same
        // updated_at each record reports, so a poller can pull only what changed.
        [HttpGet("maintenance")]
        public async Task<IActionResult> GetMaintenanceList(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromQuery(Name = "updated_since")] DateTime? updatedSince,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 500) pageSize = 100;

            var chassisList = await _db.DmsVehicleSales
                .Where(v => v.SoldTo != null && v.SoldTo.Contains(PartyMatch) && v.ChassisNo != null)
                .Select(v => v.ChassisNo!)
                .Distinct()
                .ToListAsync();

            if (chassisList.Count == 0)
                return Ok(new { total = 0, page, pageSize, records = Array.Empty<MaintenanceVehicleDto>(), _meta = MaintenanceMeta() });

            var openJobs = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo != null && chassisList.Contains(s.ChassisNo)
                            && s.JobStatus != "Closed")
                .ToListAsync();

            // One (latest) open job per chassis — same "off-road right now" scoping
            // as fleet-status/model-breakdown.
            var latestOpenJobByChassis = openJobs
                .GroupBy(s => s.ChassisNo!)
                .Select(g => g.OrderByDescending(s => s.JobDate).First())
                .ToList();

            if (updatedSince.HasValue)
            {
                latestOpenJobByChassis = latestOpenJobByChassis
                    .Where(j => (j.UpdatedAt ?? j.CreatedAt) >= updatedSince.Value)
                    .ToList();
            }

            var ordered = latestOpenJobByChassis
                .OrderByDescending(j => j.UpdatedAt ?? j.CreatedAt)
                .ToList();

            var total = ordered.Count;
            var pageJobs = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            var pageChassis = pageJobs.Select(j => j.ChassisNo!).Distinct().ToList();

            var records = await BuildMaintenanceRecords(pageChassis, pageJobs);

            return Ok(new { total, page, pageSize, records, _meta = MaintenanceMeta() });
        }

        // GET api/anvweb/maintenance/lookup?registration_number=MH12AB1234
        //   (or ?chassis_number=... — registration_number is the partner's primary
        //   match key per their spec, chassis_number is theirs as a secondary key)
        [HttpGet("maintenance/lookup")]
        public async Task<IActionResult> GetMaintenanceLookup(
            [FromHeader(Name = "X-API-Key")] string? apiKey,
            [FromQuery(Name = "registration_number")] string? registrationNumber,
            [FromQuery(Name = "chassis_number")] string? chassisNumber)
        {
            if (!IsValidApiKey(apiKey))
                return Unauthorized(new { error = "Invalid or missing API key." });

            if (string.IsNullOrWhiteSpace(registrationNumber) && string.IsNullOrWhiteSpace(chassisNumber))
                return BadRequest(new { error = "Provide registration_number or chassis_number." });

            string? chassis = string.IsNullOrWhiteSpace(chassisNumber) ? null : chassisNumber.Trim();

            if (chassis == null)
            {
                var regNo = registrationNumber!.Trim();
                var dispatchMatch = await _db.DmsVehicleDispatches
                    .Where(d => d.ChassisNo != null &&
                                ((d.RegNo != null && d.RegNo == regNo) || (d.RegNumber != null && d.RegNumber == regNo)))
                    .FirstOrDefaultAsync();
                chassis = dispatchMatch?.ChassisNo;

                if (chassis == null)
                {
                    var billMatch = await _db.RepairBills
                        .Where(b => b.ChassisNo != null && b.RegNo == regNo)
                        .FirstOrDefaultAsync();
                    chassis = billMatch?.ChassisNo;
                }

                if (chassis == null)
                    return NotFound(new { error = $"No vehicle found for registration number '{regNo}'." });
            }

            var sale = await _db.DmsVehicleSales
                .Where(v => v.ChassisNo == chassis && v.SoldTo != null && v.SoldTo.Contains(PartyMatch))
                .FirstOrDefaultAsync();
            if (sale == null)
                return NotFound(new { error = $"No ANV Web Ventures vehicle found for chassis number '{chassis}'." });

            var latestJob = await _db.DmsServiceHistories
                .Where(s => !s.IsRowTotal && s.ChassisNo == chassis)
                .OrderByDescending(s => s.JobDate)
                .FirstOrDefaultAsync();

            var records = await BuildMaintenanceRecords(new List<string> { chassis }, latestJob == null ? new() : new() { latestJob });
            var record = records.FirstOrDefault();
            if (record == null) return NotFound(new { error = $"No maintenance data found for chassis '{chassis}'." });

            return Ok(new { record, _meta = MaintenanceMeta() });
        }

        private static object MaintenanceMeta() => new { unavailableFields = MaintenanceUnavailableFields, note = MaintenanceGapNote };

        // Builds one MaintenanceVehicleDto per chassis in `chassisList`, batching
        // the Dispatch/Proforma/RepairBill/StockSummary lookups instead of querying
        // per-vehicle. `latestJobs` is the (possibly empty) latest-open-job list
        // already fetched by the caller — a chassis with no open job still gets a
        // record (e.g. from the lookup endpoint), just with maintenance_type/
        // final_status derived from its most recent job of any status if present.
        private async Task<List<MaintenanceVehicleDto>> BuildMaintenanceRecords(
            List<string> chassisList, List<AutoGeniusSync.Models.DmsServiceHistory> latestJobs)
        {
            var jobByChassis = latestJobs
                .Where(j => j.ChassisNo != null)
                .GroupBy(j => j.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First());

            // Fall back to each chassis's most recent job of ANY status when the
            // caller didn't already supply an open one (the lookup endpoint case).
            var missing = chassisList.Where(c => !jobByChassis.ContainsKey(c)).ToList();
            if (missing.Count > 0)
            {
                var anyJobs = await _db.DmsServiceHistories
                    .Where(s => !s.IsRowTotal && s.ChassisNo != null && missing.Contains(s.ChassisNo))
                    .ToListAsync();
                foreach (var g in anyJobs.GroupBy(j => j.ChassisNo!))
                    jobByChassis[g.Key] = g.OrderByDescending(j => j.JobDate).First();
            }

            var dispatches = await _db.DmsVehicleDispatches
                .Where(d => d.ChassisNo != null && chassisList.Contains(d.ChassisNo))
                .ToListAsync();
            var dispatchByChassis = dispatches
                .GroupBy(d => d.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First());

            var sales = await _db.DmsVehicleSales
                .Where(v => v.ChassisNo != null && chassisList.Contains(v.ChassisNo))
                .ToListAsync();
            var saleByChassis = sales
                .GroupBy(v => v.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.First());

            // Latest Proforma (estimate) per chassis, with its line items — this is
            // where parts_required and the assessment_status proxy come from.
            var proformas = await _db.Proformas
                .Include(p => p.Items)
                .Where(p => p.ChassisNo != null && chassisList.Contains(p.ChassisNo))
                .ToListAsync();
            var latestProformaByChassis = proformas
                .GroupBy(p => p.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.InvoiceDate ?? p.CreatedAt).First());

            // Every RepairBill per chassis, with items (to compute each invoice's
            // amount as the sum of its line items) — this is the invoices[] list.
            var repairBills = await _db.RepairBills
                .Include(b => b.Items)
                .Where(b => b.ChassisNo != null && chassisList.Contains(b.ChassisNo))
                .ToListAsync();
            var repairBillsByChassis = repairBills
                .GroupBy(b => b.ChassisNo!)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.InvoiceDate).ToList());

            // Stock lookup for parts_required[].available — matches Proforma item
            // ItemCode against DMS_StockSummaryDetail.PartNo for the same dealer.
            // ASSUMPTION: both come from the same ERP part-numbering scheme, so
            // ItemCode == PartNo lines up; flag it if that turns out not to hold.
            var stockKeys = proformas
                .SelectMany(p => p.Items.Select(i => (DealerCode: p.DealerCode, PartNo: i.ItemCode)))
                .Where(k => !string.IsNullOrEmpty(k.DealerCode) && !string.IsNullOrEmpty(k.PartNo))
                .Distinct()
                .ToList();
            var dealerCodes = stockKeys.Select(k => k.DealerCode!).Distinct().ToList();
            var partNos = stockKeys.Select(k => k.PartNo!).Distinct().ToList();
            var stockRows = dealerCodes.Count == 0 || partNos.Count == 0
                ? new List<AutoGeniusSync.Models.DmsStockSummaryDetail>()
                : await _db.DmsStockSummaryDetails
                    .Where(s => s.DealerCode != null && dealerCodes.Contains(s.DealerCode)
                                && partNos.Contains(s.PartNo))
                    .ToListAsync();
            var stockByKey = stockRows
                .GroupBy(s => (s.DealerCode, s.PartNo))
                .ToDictionary(g => g.Key, g => g.First().BalQty);

            var results = new List<MaintenanceVehicleDto>();
            foreach (var chassis in chassisList)
            {
                jobByChassis.TryGetValue(chassis, out var job);
                dispatchByChassis.TryGetValue(chassis, out var dispatch);
                saleByChassis.TryGetValue(chassis, out var sale);
                latestProformaByChassis.TryGetValue(chassis, out var proforma);
                repairBillsByChassis.TryGetValue(chassis, out var bills);

                var parts = (proforma?.Items ?? Enumerable.Empty<AutoGeniusSync.Models.DmsProformaItem>())
                    .Select(i =>
                    {
                        bool? available = null;
                        if (proforma?.DealerCode != null && i.ItemCode != null &&
                            stockByKey.TryGetValue((proforma.DealerCode, i.ItemCode), out var balQty))
                            available = balQty > 0;

                        return new MaintenancePartDto
                        {
                            Name = i.ItemDesc,
                            PartNo = i.ItemCode,
                            UnitPrice = i.Rate,
                            Qty = i.Qty,
                            InsuranceCovered = null, // no reliable source — see class doc
                            WarrantyCovered = i.IssueType == null ? (bool?)null : i.IssueType == "U/W",
                            Available = available
                        };
                    })
                    .ToList();

                var invoices = (bills ?? new())
                    .Select(b => new MaintenanceInvoiceDto
                    {
                        InvoiceNo = b.InvoiceNo?.ToString(),
                        Amount = b.Items.Sum(i => i.TotAmnt ?? 0m),
                        Date = b.InvoiceDate.HasValue ? DateOnly.FromDateTime(b.InvoiceDate.Value) : null,
                        FileUrl = null // no document storage in AutoGeniusSync
                    })
                    .ToList();

                var updatedCandidates = new List<DateTime?>();
                if (job != null) updatedCandidates.Add(job.UpdatedAt ?? job.CreatedAt);
                if (proforma != null) updatedCandidates.Add(proforma.UpdatedAt ?? proforma.CreatedAt);
                if (bills != null && bills.Count > 0) updatedCandidates.Add(bills.Max(b => b.UpdatedAt ?? b.CreatedAt));

                var validDates = updatedCandidates.Where(d => d.HasValue).Select(d => d!.Value).ToList();
                DateTime? updatedAt = validDates.Count > 0 ? validDates.Max() : null;

                results.Add(new MaintenanceVehicleDto
                {
                    RegistrationNumber = dispatch?.RegNo ?? dispatch?.RegNumber,
                    ChassisNumber = chassis,
                    MaintenanceType = job == null ? null : ClassifyMaintenanceType(job.RepairType, job.JobType, job.JobCategory),
                    ExpectedRfd = null,   // not tracked
                    VehicleAtHub = null,  // not tracked
                    HubLocation = dispatch?.Location ?? job?.Location ?? sale?.Location,
                    PartnerRemark = null, // not tracked
                    AssessmentStatus = proforma != null ? "assessed" : "pending", // proxy — see class doc
                    FinalStatus = job == null ? null : (job.JobStatus == "Closed" ? "ready_for_dispatch" : "in_progress"),
                    PartsRequired = parts,
                    Invoices = invoices,
                    UpdatedAt = updatedAt
                });
            }

            return results;
        }
    }
}