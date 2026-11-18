using System.Data;
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AutoGeniusSync.Services;

// ─────────────────────────────────────────────────────────────
// CURRENT FINANCIAL YEAR — FULL REFRESH (Sync:Mode = "FyRefresh"), every night from 12:00 AM IST.
//
// For ServiceHistory, VehicleSales, VehicleDispatches and LineOrderReport:
//   FY start  = 1 April of the financial year that contains YESTERDAY (Apr–Mar). Example: run on
//               18.11.2026 → 01.04.2026 … 17.11.2026. (A run on 01.04.2027 refreshes the closing FY
//               01.04.2026 … 31.03.2027 one last time.)
//   Rows dated before FY start are never touched. Nothing is fetched before FY start (no 2020 load).
//
// SAFE REPLACE — the live tables are never emptied:
//   1. FETCH   every unit (dealer × month, or all-dealers × day where configured) from the ERP into a
//              separate staging table  DMS_<table>_FyStage. The live table is NOT touched in this phase.
//              Progress is kept per unit in DMS_SyncCheckpoint (ApiName "FY_<table>"), so after an IIS stop /
//              recycle / reboot the run continues with the units still missing (same staging data).
//   2. CHECK   a table is replaced ONLY when ALL of its units were fetched successfully AND the staged
//              row count is plausible (≥ FyRefresh:MinRowRatio of the rows it replaces) AND the staged
//              rows really belong to the FY date range (detects a wrong FyRefresh:DateColumns setting).
//   3. SWAP    one SQL transaction per table: DELETE the table's FY rows (date column between FY start
//              and yesterday) + rows with the same UniqueKey, INSERT the staged rows, COMMIT.
//              Any error → ROLLBACK: yesterday's data stays exactly as it was.
//   A failed ERP fetch therefore never leaves FY data deleted or half-loaded: the table simply keeps the
//   previous night's data and the run reports which units failed (they are retried on the next attempt).
// ─────────────────────────────────────────────────────────────
public partial class DataSyncService
{
    internal const string FyApiPrefix = "FY_";
    private const string FyUnitColumn = "FyUnitKey";

    // Faster reports first; the slow job report (DJR → ServiceHistory) last.
    private static readonly string[] FyApiOrder =
        { SyncApi.VehicleSales, SyncApi.VehicleDispatches, SyncApi.LineOrderReport, SyncApi.ServiceHistory };

    internal static DateOnly FinancialYearStart(DateOnly d)
        => d.Month >= 4 ? new DateOnly(d.Year, 4, 1) : new DateOnly(d.Year - 1, 4, 1);

    // Date column that decides "this row belongs to the current FY". Must be the date the ERP report filters
    // on; a mismatch is detected before any delete (see CHECK above). Override: FyRefresh:DateColumns:<Api>.
    private string FyDateProperty(string api) => _config[$"FyRefresh:DateColumns:{api}"] ?? api switch
    {
        SyncApi.ServiceHistory    => "JobDate",
        SyncApi.VehicleSales      => "InvoiceDate",
        SyncApi.VehicleDispatches => "SaleDate",
        SyncApi.LineOrderReport   => "JobDate",
        _ => throw new ArgumentException(api)
    };

    private static Type FyEntity(string api) => api switch
    {
        SyncApi.ServiceHistory    => typeof(DmsServiceHistory),
        SyncApi.VehicleSales      => typeof(DmsVehicleSale),
        SyncApi.VehicleDispatches => typeof(DmsVehicleDispatch),
        SyncApi.LineOrderReport   => typeof(DmsLineOrderReport),
        _ => throw new ArgumentException(api)
    };

    // Table / column names straight from the EF model (no hard-coded column lists).
    private sealed record FyTableInfo(string Api, string Main, string Stage, string DateColumn,
                                      List<(System.Reflection.PropertyInfo Prop, string Column, Type ClrType)> Columns);

    private FyTableInfo GetFyTable(AppDbContext db, string api)
    {
        var et = db.Model.FindEntityType(FyEntity(api))!;
        var table = et.GetTableName()!;
        var modelSchema = et.GetSchema();                       // null = default schema in the EF model
        var schema = modelSchema ?? "dbo";
        var store = StoreObjectIdentifier.Table(table, modelSchema);

        var cols = et.GetProperties()
            .Where(p => p.PropertyInfo != null && !p.IsPrimaryKey()
                        && p.ValueGenerated != ValueGenerated.OnAddOrUpdate)       // skip computed (JobStatus)
            .Select(p => (p.PropertyInfo!, p.GetColumnName(store)!, Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType))
            .ToList();

        var dateProp = FyDateProperty(api);
        var dateCol = et.FindProperty(dateProp)?.GetColumnName(store)
                      ?? throw new InvalidOperationException($"FyRefresh:DateColumns:{api} = '{dateProp}' is not a property of {et.ClrType.Name}.");

        return new FyTableInfo(api, $"[{schema}].[{table}]", $"[{schema}].[{table}_FyStage]", dateCol, cols);
    }

    // ── entry point (called through NightlyReloadRunner, under the single-sync lock) ──
    public async Task<SyncResult> RunFyRefreshAsync(CancellationToken ct)
    {
        var overall = new SyncResult { SyncType = "FyRefresh" };
        var problems = new List<string>();
        var notes = new List<string>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var log = new DmsSyncLog { SyncType = "FyRefresh", StartedAt = DateTime.UtcNow, Status = "Running",
                                   SyncDate = DateOnly.FromDateTime(TodayIst()) };
        db.DmsSyncLogs.Add(log);
        await db.SaveChangesAsync(CancellationToken.None);

        async Task Progress(string text)
        {
            _logger.LogInformation("FY refresh — {step}", text);
            log.ErrorMessage = Trunc("Progress: " + text, 1800);
            try { await db.SaveChangesAsync(CancellationToken.None); } catch { /* progress only */ }
        }

        try
        {
            var state = await GetOrStartFyRunAsync(db, Progress, ct);
            if (state == null)
            {
                log.Status = "Success";
                log.ErrorMessage = "Today's current-FY refresh is already completed.";
                return overall;
            }

            var tables = FyApiOrder.ToDictionary(a => a, a => GetFyTable(db, a));
            var swapped = (state.SwappedTables ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

            // 1 ── FETCH into staging (resumable)
            foreach (var api in FyApiOrder.Where(a => !swapped.Contains(a)))
            {
                ct.ThrowIfCancellationRequested();
                await Progress($"fetching {api} {state.FyStart:dd.MM.yyyy}–{state.ToDate:dd.MM.yyyy} into staging");
                var r = await FetchFyUnitsAsync(api, tables[api], state, ct);
                overall.RecordsFetched += r.Fetched;
                if (r.StopReason != null) { problems.Add($"{api}: {r.StopReason}"); if (r.Fatal) break; }
            }

            // 2+3 ── CHECK and SWAP, table by table
            foreach (var api in FyApiOrder.Where(a => !swapped.Contains(a)))
            {
                ct.ThrowIfCancellationRequested();
                var notDone = await db.DmsSyncCheckpoints.AsNoTracking()
                    .CountAsync(c => c.ApiName == FyApiPrefix + api && c.Status != SyncCheckpointStatus.Completed, CancellationToken.None);
                if (notDone > 0)
                {
                    problems.Add($"{api}: NOT replaced — {notDone} unit(s) not fetched yet/failed; previous data kept, retried on the next attempt");
                    continue;
                }

                await Progress($"checking and replacing {api} FY data");
                var swap = await SwapFyTableAsync(db, tables[api], state.FyStart, state.ToDate, ct);
                if (swap.Error != null) { problems.Add($"{api}: NOT replaced — {swap.Error}"); continue; }

                overall.RecordsInserted += swap.Inserted;
                notes.Add($"{api}: replaced {swap.Deleted} FY row(s) with {swap.Inserted} fresh row(s)");
                swapped.Add(api);
                state.SwappedTables = string.Join(",", swapped);
                state.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }

            var allDone = FyApiOrder.All(swapped.Contains);
            state.Status = allDone ? "Completed" : "Fetching";
            if (allDone) state.CompletedAt = DateTime.UtcNow;
            state.Notes = Trunc(string.Join(" | ", notes.Concat(problems)), 3900);
            state.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            var summary = $"FY {state.FyStart:dd.MM.yyyy}–{state.ToDate:dd.MM.yyyy}: " + string.Join(" | ", notes);
            if (problems.Count == 0)
            {
                log.Status = "Success";
                log.ErrorMessage = Trunc("OK — " + summary, 1800);
            }
            else
            {
                log.Status = "PartialFailure";
                log.ErrorMessage = Trunc(string.Join(" | ", problems) + " || " + summary, 1800);
                overall.Error = log.ErrorMessage;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Status = "Interrupted";
            log.ErrorMessage = "Stopped (app shutdown / IIS recycle). Live data unchanged for tables not yet replaced; the run resumes from staging on the next start.";
            overall.Error = log.ErrorMessage;
            throw;
        }
        catch (Exception ex)
        {
            log.Status = "Failed";
            log.ErrorMessage = Trunc("FAILED — live data of tables not yet replaced is unchanged: " + (ex.InnerException?.Message ?? ex.Message), 1800);
            overall.Error = log.ErrorMessage;
            _logger.LogError(ex, "FY refresh failed");
        }
        finally
        {
            log.CompletedAt     = DateTime.UtcNow;
            log.RecordsFetched  = overall.RecordsFetched;
            log.RecordsInserted = overall.RecordsInserted;
            try { await db.SaveChangesAsync(CancellationToken.None); } catch (Exception e) { _logger.LogWarning("FY log save failed: {m}", e.Message); }
        }
        return overall;
    }

    // Resume an unfinished run, or start today's. Returns null when today's run is already completed.
    private async Task<DmsFyRefreshState?> GetOrStartFyRunAsync(AppDbContext db, Func<string, Task> progress, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TodayIst());
        var yesterday = today.AddDays(-1);
        var abandonAfter = TimeSpan.FromHours(Math.Max(1, _config.GetValue("FyRefresh:AbandonAfterHours", 48)));

        var last = await db.DmsFyRefreshStates.OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (last != null && last.Status == "Completed" && last.RunDate == today)
            return null;

        // Unfinished run (e.g. stopped by an IIS recycle, or still busy from last night): continue it with its
        // own date range — its staging data and completed units are kept.
        if (last != null && last.Status == "Fetching" && DateTime.UtcNow - last.StartedAt < abandonAfter)
        {
            await progress($"resuming run of {last.RunDate:dd.MM.yyyy} (FY {last.FyStart:dd.MM.yyyy}–{last.ToDate:dd.MM.yyyy})");
            return last;
        }
        if (last != null && last.Status == "Fetching")
        {
            last.Status = "Abandoned";
            last.UpdatedAt = DateTime.UtcNow;
        }

        var state = new DmsFyRefreshState
        {
            RunDate = today, FyStart = FinancialYearStart(yesterday), ToDate = yesterday,
            Status = "Fetching", StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.DmsFyRefreshStates.Add(state);
        await db.SaveChangesAsync(ct);
        await progress($"new run: FY {state.FyStart:dd.MM.yyyy}–{state.ToDate:dd.MM.yyyy}; preparing staging tables");

        // Fresh staging tables (same columns as the live table + FyUnitKey). Only *_FyStage work tables are dropped.
        foreach (var api in FyApiOrder)
        {
            var t = GetFyTable(db, api);
            var sql = $@"
IF OBJECT_ID(N'{t.Stage}', N'U') IS NOT NULL DROP TABLE {t.Stage};
SELECT TOP 0 * INTO {t.Stage} FROM {t.Main};
ALTER TABLE {t.Stage} ADD [{FyUnitColumn}] NVARCHAR(150) NULL;
CREATE INDEX [IX_FyStage_Unit] ON {t.Stage} ([{FyUnitColumn}]);
CREATE INDEX [IX_FyStage_Key] ON {t.Stage} ([UniqueKey]);";
#pragma warning disable EF1002 // names come from the EF model, not from input
            await db.Database.ExecuteSqlRawAsync(sql, ct);
#pragma warning restore EF1002
        }

        // Units for this run
        await db.DmsSyncCheckpoints.Where(c => c.ApiName.StartsWith(FyApiPrefix)).ExecuteDeleteAsync(ct);
        var dealers = await GetSyncDealerCodesAsync(ct);
        if (dealers.Count == 0)
        {
            await SyncAllDealersAsync();
            dealers = await GetSyncDealerCodesAsync(ct);
        }
        var perDayApis = (_config.GetSection("FyRefresh:AllDealersPerDayApis").Get<string[]>() ?? Array.Empty<string>())
            .Select(SyncApi.Normalize).Where(a => a != null && a != SyncApi.LineOrderReport).ToHashSet();   // LOR needs a dealer

        var now = DateTime.UtcNow;
        var units = new List<DmsSyncCheckpoint>();
        foreach (var api in FyApiOrder)
        {
            if (perDayApis.Contains(api))
            {
                for (var d = state.FyStart; d <= state.ToDate; d = d.AddDays(1))
                    units.Add(NewUnit(api, "", d, d, now));
            }
            else
            {
                for (var m = state.FyStart; m <= state.ToDate; m = m.AddMonths(1))
                {
                    var end = m.AddMonths(1).AddDays(-1);
                    if (end > state.ToDate) end = state.ToDate;
                    foreach (var dealer in dealers) units.Add(NewUnit(api, dealer, m, end, now));
                }
            }
        }
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var chunk in units.Chunk(1000))
        {
            db.DmsSyncCheckpoints.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            foreach (var u in chunk) db.Entry(u).State = EntityState.Detached;
        }
        db.ChangeTracker.AutoDetectChangesEnabled = true;

        _logger.LogInformation("FY refresh {from:dd.MM.yyyy}–{to:dd.MM.yyyy}: {n} ERP unit(s) planned ({d} dealers; all-dealer per-day APIs: {pd}).",
            state.FyStart, state.ToDate, units.Count, dealers.Count, perDayApis.Count == 0 ? "none" : string.Join(",", perDayApis));
        return state;
    }

    private static DmsSyncCheckpoint NewUnit(string api, string dealer, DateOnly from, DateOnly to, DateTime now) => new()
    {
        ApiName = FyApiPrefix + api, DealerCode = dealer, PeriodStart = from, PeriodEnd = to,
        Status = SyncCheckpointStatus.Pending, CreatedAt = now, UpdatedAt = now
    };

    private sealed record FyFetchResult(int Fetched, string? StopReason, bool Fatal);

    // Fetch all pending units of one table into staging. Two passes: failed units get one more try at the end.
    private async Task<FyFetchResult> FetchFyUnitsAsync(string api, FyTableInfo table, DmsFyRefreshState state, CancellationToken ct)
    {
        var maxAttempts      = Math.Max(1, _config.GetValue("HistoricalSync:MaxAttemptsPerPeriod", 5));
        var unavailableLimit = Math.Max(1, _config.GetValue("HistoricalSync:ErpUnavailableConsecutiveLimit", 3));
        var coolDown         = TimeSpan.FromMinutes(Math.Max(1, _config.GetValue("HistoricalSync:ErpCoolDownMinutes", 10)));
        var maxCoolDowns     = Math.Max(0, _config.GetValue("HistoricalSync:MaxCoolDownsPerRun", 3));
        int fetched = 0, consecutive = 0, coolDowns = 0;

        for (var pass = 1; pass <= 2; pass++)
        {
            List<DmsSyncCheckpoint> work;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                work = await db.DmsSyncCheckpoints.AsNoTracking()
                    .Where(c => c.ApiName == FyApiPrefix + api && c.Status != SyncCheckpointStatus.Completed && c.Attempts < maxAttempts)
                    .OrderBy(c => c.PeriodStart).ThenBy(c => c.DealerCode)
                    .ToListAsync(ct);
            }
            if (work.Count == 0) break;
            _logger.LogInformation("FY refresh — {api}: {n} unit(s) to fetch (pass {p}).", api, work.Count, pass);

            var done = 0;
            foreach (var cp in work)
            {
                ct.ThrowIfCancellationRequested();
                var r = await ProcessFyUnitAsync(api, table, cp, ct);
                fetched += r.Fetched;
                if (r.Error == null) consecutive = 0;
                else
                {
                    if (r.AuthError) return new FyFetchResult(fetched, "ERP refused authorization — run stopped", true);
                    if (r.ErpUnavailable && ++consecutive >= unavailableLimit)
                    {
                        if (coolDowns >= maxCoolDowns)
                            return new FyFetchResult(fetched, "ERP unavailable repeatedly — fetching stopped, resumes on the next attempt", true);
                        coolDowns++;
                        _logger.LogWarning("FY refresh — ERP unavailable {n}x in a row; pausing {m} min.", consecutive, coolDown.TotalMinutes);
                        await Task.Delay(coolDown, ct);
                        consecutive = 0;
                    }
                }
                if (++done % 50 == 0)
                    _logger.LogInformation("FY refresh — {api}: {done}/{n} unit(s) processed (pass {p}).", api, done, work.Count, pass);
            }
        }
        return new FyFetchResult(fetched, null, false);
    }

    private async Task<PeriodResult> ProcessFyUnitAsync(string api, FyTableInfo table, DmsSyncCheckpoint cp, CancellationToken ct)
    {
        var unitKey = $"{api}|{cp.DealerCode}|{cp.PeriodStart:yyyyMMdd}";
        var now = DateTime.UtcNow;
        await UpdateCheckpointAsync(cp.Id, s => s
            .SetProperty(c => c.Status, SyncCheckpointStatus.InProgress)
            .SetProperty(c => c.Attempts, c => c.Attempts + 1)
            .SetProperty(c => c.StartedAt, now)
            .SetProperty(c => c.UpdatedAt, now), ct);

        var counts = new UpsertCounts();
        try
        {
            // A unit interrupted half-way left some rows in staging — remove them before fetching again.
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM {table.Stage} WHERE [{FyUnitColumn}] = {{0}}", new object[] { unitKey }, ct);
#pragma warning restore EF1002
            }

            var token = await _erpApi.GetValidTokenAsync();
            await FetchAndUpsertWindowAsync(api, cp.DealerCode,
                cp.PeriodStart.ToDateTime(TimeOnly.MinValue), cp.PeriodEnd.ToDateTime(TimeOnly.MinValue),
                cp.PeriodEnd, cp.DealerCode.Length > 0, token, counts, ct,
                (type, rows, _, _, c, t) => BulkCopyToStageAsync(table, type, rows, unitKey, c, t));

            var doneAt = DateTime.UtcNow;
            await UpdateCheckpointAsync(cp.Id, s => s
                .SetProperty(c => c.Status, SyncCheckpointStatus.Completed)
                .SetProperty(c => c.RecordsFetched, counts.Fetched)
                .SetProperty(c => c.RecordsInserted, counts.Inserted)
                .SetProperty(c => c.RecordsSkipped, counts.Skipped)
                .SetProperty(c => c.LastError, (string?)null)
                .SetProperty(c => c.CompletedAt, doneAt)
                .SetProperty(c => c.UpdatedAt, doneAt), CancellationToken.None);
            return new PeriodResult(counts.Fetched, counts.Inserted, 0, 0, counts.Skipped, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // unit stays InProgress → fetched again on resume (its staged rows are deleted first)
        }
        catch (Exception ex)
        {
            var msg = Trunc(ex is ErpUnavailableException or ErpAuthorizationException or ErpInvalidResponseException or ErpServerErrorException
                ? ex.Message : (ex.InnerException?.Message ?? ex.Message), 1900);
            _logger.LogWarning("FY refresh {api} {from:dd.MM.yyyy} dealer '{dc}' FAILED: {msg}", api, cp.PeriodStart, cp.DealerCode, msg);
            var failedAt = DateTime.UtcNow;
            try
            {
                await UpdateCheckpointAsync(cp.Id, s => s
                    .SetProperty(c => c.Status, SyncCheckpointStatus.Failed)
                    .SetProperty(c => c.LastError, msg)
                    .SetProperty(c => c.UpdatedAt, failedAt), CancellationToken.None);
            }
            catch (Exception saveEx) { _logger.LogError(saveEx, "Could not record FY unit failure {id}", cp.Id); }
            return new PeriodResult(counts.Fetched, 0, 0, 0, 0, msg,
                AuthError: ex is ErpAuthorizationException || IsErpAuthError(ex.Message),
                ErpUnavailable: ex is ErpUnavailableException or ErpServerErrorException);
        }
    }

    // Rows of one ERP window → staging table, via SqlBulkCopy (fast, constant memory, no change tracking).
    private async Task BulkCopyToStageAsync(FyTableInfo table, Type type, System.Collections.IList rows, string unitKey,
                                            UpsertCounts counts, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var list = rows.Cast<IErpSyncRow>().GroupBy(r => r.UniqueKey ?? Guid.NewGuid().ToString(), StringComparer.Ordinal)
                       .Select(g => g.Last()).ToList();
        counts.Skipped += rows.Count - list.Count;
        rows.Clear();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meta = RowMeta.For(db, type);
        var now = DateTime.UtcNow;

        using var dt = new DataTable();
        foreach (var c in table.Columns)
            dt.Columns.Add(c.Column, c.ClrType == typeof(DateOnly) ? typeof(DateTime) : c.ClrType);
        dt.Columns.Add(FyUnitColumn, typeof(string));

        foreach (var r in list)
        {
            r.RowHash = meta.Hash(r);
            r.CreatedAt = now;
            r.UpdatedAt = now;
            var values = new object[table.Columns.Count + 1];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var v = table.Columns[i].Prop.GetValue(r);
                values[i] = v switch
                {
                    null => DBNull.Value,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    _ => v
                };
            }
            values[^1] = unitKey;
            dt.Rows.Add(values);
        }

        var conn = (SqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = table.Stage, BulkCopyTimeout = 600, BatchSize = 5000 };
        foreach (DataColumn c in dt.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
        counts.Inserted += list.Count;
    }

    private sealed record FySwapResult(int Deleted, int Inserted, string? Error);

    private async Task<FySwapResult> SwapFyTableAsync(AppDbContext db, FyTableInfo t, DateOnly fyStart, DateOnly toDate, CancellationToken ct)
    {
        var minRatio  = _config.GetValue("FyRefresh:MinRowRatio", 0.5);
        var maxOutside = _config.GetValue("FyRefresh:MaxOutOfRangeRatio", 0.02);
        var conn = (SqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

        SqlCommand Cmd(string sql, int timeout = 600)
        {
            var c = new SqlCommand(sql, conn) { CommandTimeout = timeout };
            c.Parameters.Add("@fy", SqlDbType.Date).Value = fyStart.ToDateTime(TimeOnly.MinValue);
            c.Parameters.Add("@to", SqlDbType.Date).Value = toDate.ToDateTime(TimeOnly.MinValue);
            return c;
        }

        var dc = $"[{t.DateColumn}]";
        long staged, existing, outside;
        await using (var c = Cmd($@"
SELECT
  (SELECT COUNT(*) FROM (SELECT UniqueKey, ROW_NUMBER() OVER (PARTITION BY UniqueKey ORDER BY Id DESC) rn FROM {t.Stage}) x
     WHERE x.UniqueKey IS NULL OR x.rn = 1),
  (SELECT COUNT(*) FROM {t.Main} WHERE {dc} >= @fy AND {dc} <= @to),
  (SELECT COUNT(*) FROM {t.Stage} WHERE {dc} IS NULL OR {dc} < @fy OR {dc} > @to);"))
        await using (var rd = await c.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            staged = Convert.ToInt64(rd[0]); existing = Convert.ToInt64(rd[1]); outside = Convert.ToInt64(rd[2]);
        }

        if (existing > 0 && staged < existing * minRatio)
            return new FySwapResult(0, 0,
                $"only {staged} row(s) fetched vs {existing} in the table for this FY (< {minRatio:P0}, FyRefresh:MinRowRatio) — looks like an incomplete ERP answer; previous data kept");
        if (staged > 0 && outside > staged * maxOutside)
            return new FySwapResult(0, 0,
                $"{outside} of {staged} fetched row(s) have {t.DateColumn} outside {fyStart:dd.MM.yyyy}–{toDate:dd.MM.yyyy} — " +
                $"the ERP filters on a different date; set FyRefresh:DateColumns:{t.Api}. Previous data kept");

        var cols = string.Join(", ", t.Columns.Select(c => $"[{c.Column}]"));
        var swapSql = $@"
SET XACT_ABORT ON;
DECLARE @del1 INT, @del2 INT, @ins INT;
BEGIN TRANSACTION;
DELETE FROM {t.Main} WHERE {dc} >= @fy AND {dc} <= @to;
SET @del1 = @@ROWCOUNT;
DELETE m FROM {t.Main} m
 WHERE m.UniqueKey IS NOT NULL AND EXISTS (SELECT 1 FROM {t.Stage} s WHERE s.UniqueKey = m.UniqueKey);
SET @del2 = @@ROWCOUNT;
INSERT INTO {t.Main} ({cols})
SELECT {cols} FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY UniqueKey ORDER BY Id DESC) AS __rn FROM {t.Stage}) x
 WHERE x.UniqueKey IS NULL OR x.__rn = 1;
SET @ins = @@ROWCOUNT;
COMMIT TRANSACTION;
SELECT @del1 + @del2, @ins;";
        try
        {
            await using var c = Cmd(swapSql, timeout: Math.Max(300, _config.GetValue("FyRefresh:SwapTimeoutSeconds", 3600)));
            await using var rd = await c.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            var deleted = Convert.ToInt32(rd[0]); var inserted = Convert.ToInt32(rd[1]);
            await rd.CloseAsync();

            // free the staging space of this table (work table only)
            await using (var tr = new SqlCommand($"TRUNCATE TABLE {t.Stage};", conn) { CommandTimeout = 300 })
                await tr.ExecuteNonQueryAsync(CancellationToken.None);

            _logger.LogInformation("FY refresh — {api}: replaced {del} row(s) with {ins} fresh row(s) ({from:dd.MM.yyyy}–{to:dd.MM.yyyy}).",
                t.Api, deleted, inserted, fyStart, toDate);
            return new FySwapResult(deleted, inserted, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // XACT_ABORT + no COMMIT reached → SQL Server rolled the whole replacement back.
            _logger.LogError(ex, "FY refresh — {api}: replacement rolled back, previous data kept.", t.Api);
            return new FySwapResult(0, 0, "database error during replace, rolled back, previous data kept: " + Trunc(ex.Message, 300));
        }
    }

    // GET api/admin/reload/fy — status of the current FY refresh.
    public async Task<object> GetFyRefreshStatusAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var last = await db.DmsFyRefreshStates.AsNoTracking().OrderByDescending(s => s.Id).Take(5).ToListAsync(ct);
        var units = await db.DmsSyncCheckpoints.AsNoTracking()
            .Where(c => c.ApiName.StartsWith(FyApiPrefix))
            .GroupBy(c => new { c.ApiName, c.Status })
            .Select(g => new { g.Key.ApiName, g.Key.Status, Units = g.Count(), Rows = g.Sum(x => (long)x.RecordsFetched) })
            .OrderBy(x => x.ApiName).ThenBy(x => x.Status)
            .ToListAsync(ct);
        return new { runs = last, units };
    }
}
