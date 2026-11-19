using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Helpers;
using AutoGeniusSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.Services;

// Options for one checkpoint run.
//   Apis                      : any of SyncApi.All
//   From / To                 : date range; aligned to whole calendar months
//   RefreshCompletedBeforeUtc : re-fetch months that were completed BEFORE this instant (nightly refresh).
//                               null = only months that are not completed yet (or were completed while
//                               the month was still running — "open" months are always re-fetched).
//   PerDealer                 : true = one ERP call per dealer per month (DealerCode filter);
//                               false = one call per month for all dealers (DealerCode = "").
//                               LineOrderReport is always per dealer (the ERP needs a dealer code).
//   DeadlineUtc               : stop cleanly before this time (so the nightly run is never blocked).
public sealed record CheckpointRunOptions(
    IReadOnlyCollection<string> Apis,
    DateTime From,
    DateTime To,
    bool PerDealer,
    DateTime? RefreshCompletedBeforeUtc = null,
    DateTime? DeadlineUtc = null,
    string Label = "Historical");

public partial class DataSyncService
{
    // ─────────────────────────────────────────────────────────
    // CHECKPOINT SYNC ENGINE — month-wise, dealer-wise, resumable, UPSERT
    //
    //  for each API → for each month (oldest first) → for each dealer:
    //     1. checkpoint row → InProgress (Attempts+1)                 [committed]
    //     2. ERP call for that dealer + month (size-capped; window split in halves on timeout,
    //        "response too large" or malformed JSON, down to 1 day)
    //     3. batched UPSERT, 500 rows per transaction: new → INSERT, changed (RowHash differs) →
    //        UPDATE, unchanged → nothing                                [committed]
    //     4. checkpoint → Completed with counts — ONLY after step 3 committed.
    //        ERP failure → Failed + LastError (never Completed); retried on the next run.
    //  A stop at any point (IIS recycle, app pool stop, reboot) leaves the month InProgress; the next
    //  run starts it again. Rows already written are matched by UniqueKey, so nothing is duplicated
    //  and nothing completed earlier is fetched again.
    // ─────────────────────────────────────────────────────────

    private const int UpsertBatchSize = 500;

    public async Task<SyncResult> RunCheckpointSyncAsync(CheckpointRunOptions opt, CancellationToken ct)
    {
        var total = new SyncResult { SyncType = "Checkpoint" + opt.Label, Date = opt.To };
        var failures = new List<string>();
        int completed = 0, failed = 0, emptyMonths = 0, unchangedRows = 0, skippedRows = 0;
        string? stopReason = null;

        var fromMonth = new DateOnly(opt.From.Year, opt.From.Month, 1);
        var toDate    = DateOnly.FromDateTime(opt.To);
        var toMonth   = new DateOnly(toDate.Year, toDate.Month, 1);
        if (toMonth < fromMonth)
        {
            total.Error = $"Invalid range {opt.From:yyyy-MM-dd}..{opt.To:yyyy-MM-dd}.";
            return total;
        }

        var maxAttempts      = Math.Max(1, _config.GetValue("HistoricalSync:MaxAttemptsPerPeriod", 5));
        var unavailableLimit = Math.Max(1, _config.GetValue("HistoricalSync:ErpUnavailableConsecutiveLimit", 3));
        var coolDown         = TimeSpan.FromMinutes(Math.Max(1, _config.GetValue("HistoricalSync:ErpCoolDownMinutes", 10)));
        var maxCoolDowns     = Math.Max(0, _config.GetValue("HistoricalSync:MaxCoolDownsPerRun", 3));
        var consecutiveUnavailable = 0;
        var coolDowns = 0;

        List<string> allDealers;
        try
        {
            allDealers = await GetSyncDealerCodesAsync(ct);
            if (allDealers.Count == 0 && opt.PerDealer)
            {
                // Dealer-wise sync needs dealer codes: load DMS_Dealers once from the ERP pincode API.
                _logger.LogWarning("{label} sync — DMS_Dealers is empty; loading dealers first.", opt.Label);
                await SyncAllDealersAsync();
                allDealers = await GetSyncDealerCodesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            total.Error = "Could not read dealer list: " + (ex.InnerException?.Message ?? ex.Message);
            return total;
        }

        foreach (var api in opt.Apis)
        {
            if (stopReason != null) break;

            // Dealer-wise is the default for every API: the ERP itself runs out of memory when DJR is asked for
            // ALL dealers for a month (HTTP 200, Valid=false, "System.OutOfMemoryException at
            // ClassERPReport.DailyJobs"), while the same month per dealer answers in ~3 s.
            var perDealer = opt.PerDealer || api == SyncApi.LineOrderReport;
            var dealers   = perDealer ? allDealers : new List<string> { "" };
            if (dealers.Count == 0)
            {
                failures.Add($"{api}: no dealer codes (DMS_Dealers empty and HistoricalSync:DealerCodes not set) — skipped");
                continue;
            }

            await SeedCheckpointsAsync(api, dealers, fromMonth, toMonth, ct);

            var work = await GetPendingPeriodsAsync(api, dealers, fromMonth, toMonth, opt.RefreshCompletedBeforeUtc, maxAttempts, ct);
            _logger.LogInformation("{label} sync — {api}: {n} month×dealer period(s) to process ({from:yyyy-MM} → {to:yyyy-MM}, {d} dealer unit(s)).",
                opt.Label, api, work.Count, fromMonth, toMonth, dealers.Count);

            var done = 0;
            foreach (var cp in work)
            {
                ct.ThrowIfCancellationRequested();
                if (opt.DeadlineUtc.HasValue && DateTime.UtcNow >= opt.DeadlineUtc.Value)
                {
                    stopReason = "time slice ended (yielding to the scheduled nightly sync); remaining periods resume on the next run";
                    break;
                }

                var r = await ProcessCheckpointAsync(api, cp, toDate, perDealer, ct);
                done++;
                total.RecordsFetched  += r.Fetched;
                total.RecordsInserted += r.Inserted;
                total.RecordsUpdated  += r.Updated;
                unchangedRows += r.Unchanged;
                skippedRows   += r.Skipped;

                if (r.Error == null)
                {
                    completed++;
                    if (r.Fetched == 0) emptyMonths++;
                    consecutiveUnavailable = 0;
                }
                else
                {
                    failed++;
                    if (failures.Count < 50)
                        failures.Add($"{api} {cp.PeriodStart:yyyy-MM} {(cp.DealerCode.Length == 0 ? "(all dealers)" : cp.DealerCode)}: {r.Error}");

                    if (r.AuthError)
                    {
                        stopReason = "ERP refused authorization — sync stopped (no point calling it for every period)";
                        break;
                    }

                    if (r.ErpUnavailable && ++consecutiveUnavailable >= unavailableLimit)
                    {
                        if (coolDowns >= maxCoolDowns)
                        {
                            stopReason = $"ERP unavailable (HTTP 5xx) {consecutiveUnavailable} times in a row after {coolDowns} cool-down(s) — stopped; resumes automatically on the next run";
                            break;
                        }
                        coolDowns++;
                        _logger.LogWarning("{label} sync — ERP unavailable {n} times in a row; pausing {min} min so the ERP app pool can recover (cool-down {c}/{max}).",
                            opt.Label, consecutiveUnavailable, coolDown.TotalMinutes, coolDowns, maxCoolDowns);
                        await Task.Delay(coolDown, ct);
                        consecutiveUnavailable = 0;
                    }
                }

                if (done % 25 == 0)
                    _logger.LogInformation("{label} sync — {api}: {done}/{n} periods processed (completed {c}, failed {f}).",
                        opt.Label, api, done, work.Count, completed, failed);
            }
        }

        var summary = $"{opt.Label}: periods completed={completed} (of which empty={emptyMonths}), failed={failed}; " +
                      $"rows fetched={total.RecordsFetched}, inserted={total.RecordsInserted}, updated={total.RecordsUpdated}, " +
                      $"unchanged={unchangedRows}, skipped={skippedRows}";
        if (stopReason != null) summary += $" | STOPPED: {stopReason}";

        if (failed > 0 || (stopReason != null && !stopReason.StartsWith("time slice", StringComparison.Ordinal)))
            total.Error = Trunc(summary + (failures.Count > 0 ? " | " + string.Join(" | ", failures) : ""), 1800);

        _logger.LogInformation("{summary}", summary);
        return total;
    }

    // True when at least one checkpoint period still needs work (used by the hosted service to decide
    // whether to start / resume the historical sync after a restart).
    public async Task<bool> HasPendingHistoricalWorkAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var maxAttempts = Math.Max(1, _config.GetValue("HistoricalSync:MaxAttemptsPerPeriod", 5));
        var retryAfter  = DateTime.UtcNow.AddHours(-24);

        // Only the units the historical run itself processes (per-dealer rows, or the "all dealers" rows
        // when HistoricalSync:PerDealer = false; LOR is always per dealer). Rows of the nightly window
        // that use the other mode are retried by the nightly run, not here.
        var q = db.DmsSyncCheckpoints.AsNoTracking().Where(c => !c.ApiName.StartsWith(FyApiPrefix));
        q = HistoricalPerDealer()
            ? q.Where(c => c.DealerCode != "")
            : q.Where(c => c.DealerCode == "" || c.ApiName == SyncApi.LineOrderReport);

        var (histTo, recheckBefore, _) = HistoricalRange();
        var toMonth = new DateOnly(histTo.Year, histTo.Month, 1);
        if (toMonth < new DateOnly(HistoricalStartDate().Year, HistoricalStartDate().Month, 1))
            return false;                                     // nothing before the current FY to load
        q = q.Where(c => c.PeriodStart <= toMonth);

        // No checkpoint rows yet = never started.
        if (!await q.AnyAsync(ct))
            return true;

        return await q.AnyAsync(c =>
            c.Status == SyncCheckpointStatus.Pending ||
            c.Status == SyncCheckpointStatus.InProgress ||
            (c.Status == SyncCheckpointStatus.Failed && (c.Attempts < maxAttempts || c.UpdatedAt < retryAfter)) ||
            (recheckBefore != null && c.Status == SyncCheckpointStatus.Completed && c.CompletedAt < recheckBefore), ct);
    }

    // Summary for GET api/admin/reload/checkpoints.
    public async Task<object> GetCheckpointSummaryAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var byApi = await db.DmsSyncCheckpoints.AsNoTracking()
            .GroupBy(c => new { c.ApiName, c.Status })
            .Select(g => new
            {
                g.Key.ApiName, g.Key.Status, Periods = g.Count(),
                Fetched = g.Sum(x => (long)x.RecordsFetched), Inserted = g.Sum(x => (long)x.RecordsInserted),
                Updated = g.Sum(x => (long)x.RecordsUpdated),
                FirstMonth = g.Min(x => x.PeriodStart), LastMonth = g.Max(x => x.PeriodStart)
            })
            .OrderBy(x => x.ApiName).ThenBy(x => x.Status)
            .ToListAsync(ct);

        var recentFailures = await db.DmsSyncCheckpoints.AsNoTracking()
            .Where(c => c.Status == SyncCheckpointStatus.Failed)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(20)
            .Select(c => new { c.ApiName, c.DealerCode, Month = c.PeriodStart, c.Attempts, c.LastError, c.UpdatedAt })
            .ToListAsync(ct);

        // "Where is it now": the oldest month per API that is not completed yet.
        var resumePoint = await db.DmsSyncCheckpoints.AsNoTracking()
            .Where(c => c.Status != SyncCheckpointStatus.Completed)
            .GroupBy(c => c.ApiName)
            .Select(g => new { ApiName = g.Key, ResumesFromMonth = g.Min(x => x.PeriodStart) })
            .ToListAsync(ct);

        return new { byApi, resumePoint, recentFailures };
    }

    // Puts periods back to Pending so they are fetched again (e.g. after a manual table truncate, or to
    // force a re-check of a range). Data is NOT deleted — the re-fetch upserts.
    public async Task<int> ResetCheckpointsAsync(string? api, DateTime? from, DateTime? to, string? dealerCode, bool failedOnly, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = db.DmsSyncCheckpoints.AsQueryable();
        if (!string.IsNullOrEmpty(api)) q = q.Where(c => c.ApiName == api);
        if (!string.IsNullOrEmpty(dealerCode)) q = q.Where(c => c.DealerCode == dealerCode);
        if (from.HasValue) { var f = new DateOnly(from.Value.Year, from.Value.Month, 1); q = q.Where(c => c.PeriodStart >= f); }
        if (to.HasValue)   { var t = DateOnly.FromDateTime(to.Value); q = q.Where(c => c.PeriodStart <= t); }
        if (failedOnly) q = q.Where(c => c.Status == SyncCheckpointStatus.Failed);

        var now = DateTime.UtcNow;
        return await q.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, SyncCheckpointStatus.Pending)
            .SetProperty(c => c.Attempts, 0)
            .SetProperty(c => c.UpdatedAt, now), ct);
    }

    // ── dealers ─────────────────────────────────────────────
    // HistoricalSync:DealerCodes (list) if set; otherwise every dealer code in DMS_Dealers
    // (HistoricalSync:ActiveDealersOnly = true limits it to ActiveStatus = 'Active').
    private async Task<List<string>> GetSyncDealerCodesAsync(CancellationToken ct)
    {
        var configured = _config.GetSection("HistoricalSync:DealerCodes").Get<List<string>>()?
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (configured is { Count: > 0 })
            return configured;

        var activeOnly = _config.GetValue("HistoricalSync:ActiveDealersOnly", false);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = db.DmsDealers.AsNoTracking().Where(d => d.DealerCode != null && d.DealerCode != "");
        if (activeOnly) q = q.Where(d => d.ActiveStatus == "Active");
        var codes = await q.Select(d => d.DealerCode!).Distinct().ToListAsync(ct);
        return codes.Select(c => c.Trim()).Where(c => c.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    // ── checkpoint rows ─────────────────────────────────────
    private async Task SeedCheckpointsAsync(string api, List<string> dealers, DateOnly fromMonth, DateOnly toMonth, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var existing = (await db.DmsSyncCheckpoints.AsNoTracking()
                .Where(c => c.ApiName == api && c.PeriodStart >= fromMonth && c.PeriodStart <= toMonth)
                .Select(c => new { c.DealerCode, c.PeriodStart })
                .ToListAsync(ct))
            .Select(x => (x.DealerCode.ToUpperInvariant(), x.PeriodStart))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var toAdd = new List<DmsSyncCheckpoint>();
        for (var m = fromMonth; m <= toMonth; m = m.AddMonths(1))
            foreach (var d in dealers)
                if (!existing.Contains((d.ToUpperInvariant(), m)))
                    toAdd.Add(new DmsSyncCheckpoint
                    {
                        ApiName = api, DealerCode = d, PeriodStart = m, PeriodEnd = m.AddMonths(1).AddDays(-1),
                        Status = SyncCheckpointStatus.Pending, CreatedAt = now, UpdatedAt = now
                    });

        foreach (var chunk in toAdd.Chunk(1000))
        {
            db.DmsSyncCheckpoints.AddRange(chunk);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _logger.LogDebug("Checkpoint seed race for {api} — rows already exist.", api);
            }
            db.ChangeTracker.Clear();
        }
        if (toAdd.Count > 0)
            _logger.LogInformation("Checkpoint: {n} new {api} period(s) registered as Pending.", toAdd.Count, api);
    }

    private async Task<List<DmsSyncCheckpoint>> GetPendingPeriodsAsync(
        string api, List<string> dealers, DateOnly fromMonth, DateOnly toMonth,
        DateTime? refreshBeforeUtc, int maxAttempts, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dealerSet = dealers.Select(d => d.ToUpperInvariant()).ToHashSet();

        var rows = await db.DmsSyncCheckpoints.AsNoTracking()
            .Where(c => c.ApiName == api && c.PeriodStart >= fromMonth && c.PeriodStart <= toMonth)
            .ToListAsync(ct);

        var nowUtc = DateTime.UtcNow;
        return rows
            .Where(c => dealerSet.Contains(c.DealerCode.ToUpperInvariant()))
            .Where(c => NeedsWork(c, refreshBeforeUtc, maxAttempts, nowUtc))
            .OrderBy(c => c.PeriodStart)
            .ThenBy(c => c.DealerCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Does this checkpoint period need to be (re)processed?
    //   Pending / InProgress (= interrupted by a stop or recycle) → yes
    //   Failed     → yes, unless it already failed maxAttempts times in the last 24 h
    //   Completed  → no, except (a) the month was not over yet when it completed ("open" month, e.g. the
    //                current month) or (b) refreshBeforeUtc is set and it completed before that instant
    internal static bool NeedsWork(DmsSyncCheckpoint c, DateTime? refreshBeforeUtc, int maxAttempts, DateTime nowUtc)
    {
        switch (c.Status)
        {
            case SyncCheckpointStatus.Completed:
                var monthClosedUtc = c.PeriodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue).AddMinutes(-330);   // 00:00 IST next day
                if (c.CompletedAt == null || c.CompletedAt < monthClosedUtc) return true;
                return refreshBeforeUtc.HasValue && c.CompletedAt < refreshBeforeUtc.Value;
            case SyncCheckpointStatus.Failed:
                return c.Attempts < maxAttempts || c.UpdatedAt < nowUtc.AddHours(-24);
            default:
                return true;
        }
    }

    private sealed record PeriodResult(int Fetched, int Inserted, int Updated, int Unchanged, int Skipped,
                                       string? Error, bool AuthError = false, bool ErpUnavailable = false);

    private async Task<PeriodResult> ProcessCheckpointAsync(string api, DmsSyncCheckpoint cp, DateOnly today, bool perDealer, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await UpdateCheckpointAsync(cp.Id, s => s
            .SetProperty(c => c.Status, SyncCheckpointStatus.InProgress)
            .SetProperty(c => c.Attempts, c => c.Attempts + 1)
            .SetProperty(c => c.StartedAt, now)
            .SetProperty(c => c.UpdatedAt, now), ct);

        var from = cp.PeriodStart.ToDateTime(TimeOnly.MinValue);
        var end  = cp.PeriodEnd > today ? today : cp.PeriodEnd;
        var to   = end.ToDateTime(TimeOnly.MinValue);
        var counts = new UpsertCounts();

        try
        {
            var token = await _erpApi.GetValidTokenAsync();
            await FetchAndUpsertWindowAsync(api, cp.DealerCode, from, to, cp.PeriodEnd, perDealer, token, counts, ct);

            // Data is committed → only now mark the period Completed.
            var doneAt = DateTime.UtcNow;
            await UpdateCheckpointAsync(cp.Id, s => s
                .SetProperty(c => c.Status, SyncCheckpointStatus.Completed)
                .SetProperty(c => c.RecordsFetched, counts.Fetched)
                .SetProperty(c => c.RecordsInserted, counts.Inserted)
                .SetProperty(c => c.RecordsUpdated, counts.Updated)
                .SetProperty(c => c.RecordsUnchanged, counts.Unchanged)
                .SetProperty(c => c.RecordsSkipped, counts.Skipped)
                .SetProperty(c => c.LastError, (string?)null)
                .SetProperty(c => c.CompletedAt, doneAt)
                .SetProperty(c => c.UpdatedAt, doneAt), CancellationToken.None);

            return new PeriodResult(counts.Fetched, counts.Inserted, counts.Updated, counts.Unchanged, counts.Skipped, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown / recycle: leave the row InProgress → resumed on the next start. Rows written so far stay
            // (they are matched by UniqueKey on the re-run, so no duplicates).
            throw;
        }
        catch (Exception ex)
        {
            var msg = Trunc(ex is ErpUnavailableException or ErpAuthorizationException or ErpInvalidResponseException or ErpServerErrorException
                ? ex.Message
                : (ex.InnerException?.Message ?? ex.Message), 1900);
            _logger.LogWarning("{api} {month:yyyy-MM} dealer '{dc}' FAILED (not marked completed): {msg}", api, cp.PeriodStart, cp.DealerCode, msg);

            var failedAt = DateTime.UtcNow;
            try
            {
                await UpdateCheckpointAsync(cp.Id, s => s
                    .SetProperty(c => c.Status, SyncCheckpointStatus.Failed)
                    .SetProperty(c => c.RecordsFetched, counts.Fetched)
                    .SetProperty(c => c.RecordsInserted, counts.Inserted)
                    .SetProperty(c => c.RecordsUpdated, counts.Updated)
                    .SetProperty(c => c.RecordsUnchanged, counts.Unchanged)
                    .SetProperty(c => c.RecordsSkipped, counts.Skipped)
                    .SetProperty(c => c.LastError, msg)
                    .SetProperty(c => c.UpdatedAt, failedAt), CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Could not record the failure of checkpoint {id}", cp.Id);
            }

            return new PeriodResult(counts.Fetched, counts.Inserted, counts.Updated, counts.Unchanged, counts.Skipped, msg,
                AuthError: ex is ErpAuthorizationException || IsErpAuthError(ex.Message),
                ErpUnavailable: ex is ErpUnavailableException or ErpServerErrorException);
        }
    }

    private async Task UpdateCheckpointAsync(long id,
        System.Linq.Expressions.Expression<Func<Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<DmsSyncCheckpoint>,
                                                 Microsoft.EntityFrameworkCore.Query.SetPropertyCalls<DmsSyncCheckpoint>>> set,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.DmsSyncCheckpoints.Where(c => c.Id == id).ExecuteUpdateAsync(set, ct);
    }

    private sealed class UpsertCounts
    {
        public int Fetched, Inserted, Updated, Unchanged, Skipped;
    }

    // Window failures worth splitting: timeout, too-large body, malformed JSON. A 503 / auth error is NOT
    // split (a smaller window does not fix an ERP outage) — it fails the period so it is retried later.
    private static bool ShouldSplit(Exception ex) =>
        ex is ErpResponseTooLargeException or TimeoutException or Newtonsoft.Json.JsonException
        || ex is ErpServerErrorException { OutOfMemory: true }
        || (ex.InnerException is TimeoutException)
        || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || IsDeterministicParseError(ex.Message)
        // ERP closed the connection while sending a big answer ("An existing connection was forcibly closed by
        // the remote host") — seen for large dealers / months (e.g. CUS0270). Retrying the same window fails the
        // same way; a smaller window (half month, down to 1 day) is a smaller answer.
        || IsConnectionDropped(ex)
        || ex.Message.Contains("JSON deserialization failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectionDropped(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is HttpRequestException or IOException or System.Net.Sockets.SocketException) return true;
            var m = e.Message;
            if (m.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
                || m.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Where fetched rows go: the live table (UPSERT, default) or the FY-refresh staging table.
    private delegate Task RowSink(Type entityType, System.Collections.IList rows, KeyLookup lookup, KeyLookup? legacy,
                                  UpsertCounts counts, CancellationToken ct);

    private async Task FetchAndUpsertWindowAsync(string api, string dealerCode, DateTime from, DateTime to,
        DateOnly periodEnd, bool perDealer, string token, UpsertCounts counts, CancellationToken ct, RowSink? sink = null)
    {
        Task Write<T>(List<T> rows, KeyLookup lookup, KeyLookup? legacy) where T : class, IErpSyncRow
            => sink == null ? UpsertRowsAsync(rows, lookup, legacy, counts, ct) : sink(typeof(T), rows, lookup, legacy, counts, ct);

        try
        {
            switch (api)
            {
                case SyncApi.ServiceHistory:
                {
                    var rows = await _erpApi.FetchReportWindowAsync<DjrValue>("djr", dealerCode, from, to, token, ct);
                    counts.Fetched += rows.Count;
                    var fallbackDate = periodEnd;
                    var entities = rows
                        .Where(j => j.JobNo != "Total")
                        .Select(j =>
                        {
                            if (perDealer && string.IsNullOrWhiteSpace(j.DealerCode)) j.DealerCode = dealerCode;
                            return j;
                        })
                        .Where(j => !string.IsNullOrEmpty(j.DealerCode) && !string.IsNullOrEmpty(j.JobNo))
                        .Select(j =>
                        {
                            var e = ParseServiceHistory(j);
                            e.JobDate ??= fallbackDate;
                            e.UniqueKey = BuildUniqueKey(e.DealerCode, e.JobNo, e.JobDate, e.ChassisNo);
                            return e;
                        })
                        .ToList();
                    counts.Skipped += rows.Count - entities.Count;
                    rows.Clear();
                    await Write(entities, LookupServiceHistoryAsync, null);
                    break;
                }
                case SyncApi.VehicleSales:
                {
                    var rows = await _erpApi.FetchReportWindowAsync<VsrValue>("vsr", dealerCode, from, to, token, ct);
                    counts.Fetched += rows.Count;
                    var entities = rows
                        .Where(v => !string.IsNullOrWhiteSpace(v.ChassisNo))
                        .Select(v =>
                        {
                            if (perDealer && string.IsNullOrWhiteSpace(v.DealerCode)) v.DealerCode = dealerCode;
                            var e = ParseVehicleSale(v);
                            e.UniqueKey = UniqueKeyBuilder.VehicleSale(e.DealerCode, e.InvoiceNo, e.ChassisNo);
                            return e;
                        })
                        .ToList();
                    counts.Skipped += rows.Count - entities.Count;
                    rows.Clear();
                    await Write(entities, LookupVehicleSalesAsync, LookupLegacyVehicleSalesAsync);
                    break;
                }
                case SyncApi.VehicleDispatches:
                {
                    var rows = await _erpApi.FetchReportWindowAsync<VdrValue>("vdr", dealerCode, from, to, token, ct);
                    counts.Fetched += rows.Count;
                    var entities = rows
                        .Where(d => !string.IsNullOrWhiteSpace(d.ChassisNo))
                        .Select(d =>
                        {
                            var e = MapVehicleDispatch(d);
                            e.UniqueKey = UniqueKeyBuilder.VehicleDispatch(e.InvoiceNo, e.ChassisNo);
                            return e;
                        })
                        .ToList();
                    counts.Skipped += rows.Count - entities.Count;
                    rows.Clear();
                    await Write(entities, LookupVehicleDispatchesAsync, LookupLegacyVehicleDispatchesAsync);
                    break;
                }
                case SyncApi.LineOrderReport:
                {
                    var rows = await _erpApi.FetchReportWindowAsync<LorValue>("lor", dealerCode, from, to, token, ct);
                    counts.Fetched += rows.Count;
                    var entities = rows
                        .Select(r =>
                        {
                            if (string.IsNullOrWhiteSpace(r.DealerCode)) r.DealerCode = dealerCode;
                            var e = MapLineOrderReport(r);
                            e.UniqueKey = BuildLorUniqueKey(e.DealerCode, e.JobNo, e.JobDate, e.ChassisNo, e.ItemName, e.ItemDescription);
                            return e;
                        })
                        .Where(p => !string.IsNullOrEmpty(p.JobNo) || p.JobDate != null || !string.IsNullOrEmpty(p.ChassisNo)
                                 || !string.IsNullOrEmpty(p.ItemName) || !string.IsNullOrEmpty(p.ItemDescription))
                        .ToList();
                    counts.Skipped += rows.Count - entities.Count;
                    rows.Clear();
                    await Write(entities, LookupLineOrderReportAsync, null);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown API '{api}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ShouldSplit(ex) && (to - from).TotalDays >= 1)
        {
            var days = (int)(to - from).TotalDays + 1;
            var mid  = from.AddDays(days / 2 - 1);
            _logger.LogWarning("{api} {from:yyyy-MM-dd}..{to:yyyy-MM-dd} dealer '{dc}': {msg} — splitting at {mid:yyyy-MM-dd}.",
                api, from, to, dealerCode, Trunc(ex.Message, 200), mid);
            await FetchAndUpsertWindowAsync(api, dealerCode, from, mid, periodEnd, perDealer, token, counts, ct, sink);
            await FetchAndUpsertWindowAsync(api, dealerCode, mid.AddDays(1), to, periodEnd, perDealer, token, counts, ct, sink);
        }
    }

    // ── batched UPSERT ──────────────────────────────────────
    // Existing row as seen by the upsert (no tracking, no full row load).
    private sealed record ExistingRow(int Id, string? UniqueKey, string? RowHash, string? ChassisNo, string? DealerCode);

    private delegate Task<List<ExistingRow>> KeyLookup(AppDbContext db, List<string> keys, CancellationToken ct);

    private static Task<List<ExistingRow>> LookupServiceHistoryAsync(AppDbContext db, List<string> keys, CancellationToken ct) =>
        db.DmsServiceHistories.AsNoTracking().Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, x.DealerCode)).ToListAsync(ct);

    private static Task<List<ExistingRow>> LookupVehicleSalesAsync(AppDbContext db, List<string> keys, CancellationToken ct) =>
        db.DmsVehicleSales.AsNoTracking().Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, x.DealerCode)).ToListAsync(ct);

    private static Task<List<ExistingRow>> LookupLegacyVehicleSalesAsync(AppDbContext db, List<string> chassis, CancellationToken ct) =>
        db.DmsVehicleSales.AsNoTracking().Where(x => x.UniqueKey == null && x.ChassisNo != null && chassis.Contains(x.ChassisNo))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, x.DealerCode)).ToListAsync(ct);

    private static Task<List<ExistingRow>> LookupVehicleDispatchesAsync(AppDbContext db, List<string> keys, CancellationToken ct) =>
        db.DmsVehicleDispatches.AsNoTracking().Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, null)).ToListAsync(ct);

    private static Task<List<ExistingRow>> LookupLegacyVehicleDispatchesAsync(AppDbContext db, List<string> chassis, CancellationToken ct) =>
        db.DmsVehicleDispatches.AsNoTracking().Where(x => x.UniqueKey == null && x.ChassisNo != null && chassis.Contains(x.ChassisNo))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, null)).ToListAsync(ct);

    private static Task<List<ExistingRow>> LookupLineOrderReportAsync(AppDbContext db, List<string> keys, CancellationToken ct) =>
        db.DmsLineOrderReports.AsNoTracking().Where(x => x.UniqueKey != null && keys.Contains(x.UniqueKey))
          .Select(x => new ExistingRow(x.Id, x.UniqueKey, x.RowHash, x.ChassisNo, x.DealerCode)).ToListAsync(ct);

    private static string NormChassis(string? c) => c?.Trim().ToUpperInvariant() ?? "";

    // INSERT new rows, UPDATE rows whose content changed (RowHash differs), leave unchanged rows alone.
    // 500 rows per transaction; the change tracker is cleared after every batch (constant memory).
    // legacyLookup: VehicleSales / VehicleDispatches rows written by the old sync have no UniqueKey —
    // such a row is ADOPTED (UniqueKey filled in) instead of inserting a second copy of the chassis.
    private async Task UpsertRowsAsync<T>(List<T> entities, KeyLookup lookup, KeyLookup? legacyLookup,
                                          UpsertCounts counts, CancellationToken ct) where T : class, IErpSyncRow
    {
        if (entities.Count == 0) return;

        // in-batch duplicates: last one wins (same rule as before)
        var deduped = entities.GroupBy(e => e.UniqueKey!, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        counts.Skipped += entities.Count - deduped.Count;
        entities.Clear();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var meta = RowMeta.For(db, typeof(T));

        foreach (var batch in deduped.Chunk(UpsertBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            foreach (var e in batch) e.RowHash = meta.Hash(e);

            var keys = batch.Select(e => e.UniqueKey!).ToList();
            var existing = (await lookup(db, keys, ct))
                .GroupBy(x => x.UniqueKey!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First(), StringComparer.Ordinal);

            Dictionary<string, ExistingRow>? legacy = null;
            if (legacyLookup != null)
            {
                var missingChassis = batch.Where(e => !existing.ContainsKey(e.UniqueKey!) && !string.IsNullOrWhiteSpace(e.ChassisNo))
                                          .SelectMany(e => new[] { e.ChassisNo!, e.ChassisNo!.Trim(), NormChassis(e.ChassisNo) })
                                          .Distinct(StringComparer.Ordinal).ToList();
                if (missingChassis.Count > 0)
                    legacy = (await legacyLookup(db, missingChassis, ct))
                        .GroupBy(x => NormChassis(x.ChassisNo))
                        .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Id).First());
            }

            var inserts = new List<T>();
            var updates = new List<(T Entity, int Id)>();
            var adopted = new HashSet<int>();

            foreach (var e in batch)
            {
                if (existing.TryGetValue(e.UniqueKey!, out var row))
                {
                    if (string.Equals(row.RowHash, e.RowHash, StringComparison.Ordinal)) { counts.Unchanged++; continue; }
                    updates.Add((e, row.Id));
                }
                else if (legacy != null && legacy.TryGetValue(NormChassis(e.ChassisNo), out var old) && adopted.Add(old.Id))
                {
                    updates.Add((e, old.Id));     // adopt the legacy row: fills UniqueKey + refreshes the data
                }
                else
                {
                    inserts.Add(e);
                }
            }

            try
            {
                foreach (var e in inserts) { e.CreatedAt = now; e.UpdatedAt = now; db.Add(e); }
                foreach (var (e, id) in updates) AttachForUpdate(db, meta, e, id, now);
                if (inserts.Count + updates.Count > 0)
                    await db.SaveChangesAsync(ct);
                counts.Inserted += inserts.Count;
                counts.Updated  += updates.Count;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another writer (e.g. the partner bulk-push API) inserted one of these keys between the lookup
                // and the save. Fall back to row-by-row for this batch — never insert a duplicate.
                db.ChangeTracker.Clear();
                _logger.LogWarning("Unique-key race in a {type} batch — retrying {n} row(s) one by one.", typeof(T).Name, inserts.Count + updates.Count);
                foreach (var (e, id) in updates)
                    await SaveSingleAsync(db, () => AttachForUpdate(db, meta, e, id, now), () => counts.Updated++, counts, ct);
                foreach (var e in inserts)
                {
                    var found = (await lookup(db, new List<string> { e.UniqueKey! }, ct)).OrderBy(x => x.Id).FirstOrDefault();
                    if (found == null)
                        await SaveSingleAsync(db, () => { e.Id = 0; e.CreatedAt = now; e.UpdatedAt = now; db.Add(e); }, () => counts.Inserted++, counts, ct);
                    else if (string.Equals(found.RowHash, e.RowHash, StringComparison.Ordinal))
                        counts.Unchanged++;
                    else
                        await SaveSingleAsync(db, () => AttachForUpdate(db, meta, e, found.Id, now), () => counts.Updated++, counts, ct);
                }
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task SaveSingleAsync(AppDbContext db, Action stage, Action onSaved, UpsertCounts counts, CancellationToken ct)
    {
        try
        {
            stage();
            await db.SaveChangesAsync(ct);
            onSaved();
        }
        catch (DbUpdateException ex)
        {
            counts.Skipped++;
            _logger.LogWarning("Row skipped during upsert: {msg}", ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    // UPDATE only the ERP-sourced columns (+ UniqueKey, RowHash, UpdatedAt). CreatedAt and columns the ERP
    // does not send (e.g. ServiceHistory.RepairType/CompletionDate, LOR.MaterialCode/MaterialDate) are kept.
    private static void AttachForUpdate<T>(AppDbContext db, RowMeta meta, T e, int id, DateTime now) where T : class, IErpSyncRow
    {
        e.Id = id;
        e.UpdatedAt = now;
        var entry = db.Entry(e);
        entry.State = EntityState.Unchanged;
        foreach (var name in meta.UpdatableColumns)
            entry.Property(name).IsModified = true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627);

    // Per-entity metadata: which columns come from the ERP (hashed + updated) — built once from the EF model.
    private sealed class RowMeta
    {
        private static readonly Dictionary<Type, RowMeta> Cache = new();
        private static readonly object CacheLock = new();

        // Columns that are NOT part of the ERP payload: never hashed, never overwritten by the sync.
        private static readonly HashSet<string> NotFromErp = new(StringComparer.Ordinal)
        {
            "Id", "CreatedAt", "UpdatedAt", "RowHash", "UniqueKey",
            "JobStatus", "RepairType", "CompletionDate",       // DMS_ServiceHistory (JobStatus is computed in SQL)
            "MaterialCode", "MaterialDate"                     // DMS_LineOrderReport
        };

        private readonly PropertyInfo[] _hashed;
        public IReadOnlyList<string> UpdatableColumns { get; }

        private RowMeta(PropertyInfo[] hashed, List<string> updatable)
        {
            _hashed = hashed;
            UpdatableColumns = updatable;
        }

        public static RowMeta For(AppDbContext db, Type t)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(t, out var m)) return m;
                var et = db.Model.FindEntityType(t) ?? throw new InvalidOperationException($"{t.Name} is not mapped.");
                var props = et.GetProperties()
                    .Where(p => p.PropertyInfo != null && !p.IsPrimaryKey() && !p.IsShadowProperty()
                                && p.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate
                                && !NotFromErp.Contains(p.Name))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                var updatable = props.Select(p => p.Name).Concat(new[] { "UniqueKey", "RowHash", "UpdatedAt" }).ToList();
                m = new RowMeta(props.Select(p => p.PropertyInfo!).ToArray(), updatable);
                Cache[t] = m;
                return m;
            }
        }

        // SHA-256 over the ERP columns in a fixed order → 64 hex chars (fits RowHash nvarchar(100)).
        public string Hash(object row)
        {
            var sb = new StringBuilder(512);
            foreach (var p in _hashed)
            {
                var v = p.GetValue(row);
                sb.Append(p.Name).Append('=');
                switch (v)
                {
                    case null: sb.Append('\u0000'); break;
                    case string s: sb.Append(s.Trim()); break;
                    case decimal d: sb.Append(d.ToString("0.############", CultureInfo.InvariantCulture)); break;
                    case DateOnly d: sb.Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
                    case DateTime d: sb.Append(d.ToString("O", CultureInfo.InvariantCulture)); break;
                    case IFormattable f: sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); break;
                    default: sb.Append(v); break;
                }
                sb.Append('|');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }
}
