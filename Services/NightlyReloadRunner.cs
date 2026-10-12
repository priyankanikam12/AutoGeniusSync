namespace AutoGeniusSync.Services;

// Snapshot of the most recent nightly reload — returned by GET api/admin/reload/status.
public sealed record ReloadStatus(
    bool IsRunning,
    string? Trigger,          // scheduled | startup | manual-api
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    string? Outcome,          // Running | Success | Problems | Interrupted | Failed
    string? Message,          // detail: failed chunks, empty tables, "FAILED BEFORE truncate", ...
    long RecordsFetched,
    long RecordsInserted);

// Single place that starts the nightly full reload. Used by BOTH the daily timer
// (SyncHostedService) and the manual / Task-Scheduler endpoint (NightlyReloadController),
// so the two can never run a reload at the same time — the second caller is told
// "already running" instead of truncating tables underneath the first one.
public class NightlyReloadRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyReloadRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private ReloadStatus _status = new(false, null, null, null, null, null, 0, 0);

    public NightlyReloadRunner(IServiceScopeFactory scopeFactory, ILogger<NightlyReloadRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsRunning { get { lock (_stateLock) return _status.IsRunning; } }

    public ReloadStatus GetStatus() { lock (_stateLock) return _status; }

    private void SetStatus(ReloadStatus s) { lock (_stateLock) _status = s; }

    // Runs the full reload unless one is already running. Returns false (and does nothing)
    // when another reload holds the gate. Never throws.
    public async Task<bool> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return false;

        var startedUtc = DateTime.UtcNow;
        try
        {
            SetStatus(new ReloadStatus(true, trigger, startedUtc, null, "Running", null, 0, 0));
            _logger.LogInformation("Nightly full reload START (trigger: {trigger}).", trigger);

            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();
            var result = await svc.RunNightlyFullReloadAsync(ct);

            var outcome = string.IsNullOrEmpty(result.Error) ? "Success" : "Problems";
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, outcome,
                result.Error, result.RecordsFetched, result.RecordsInserted));

            if (outcome == "Success")
                _logger.LogInformation("Nightly full reload END — Success (trigger: {trigger}): {fet} fetched, {ins} inserted.",
                    trigger, result.RecordsFetched, result.RecordsInserted);
            else
                _logger.LogError("Nightly full reload END — completed WITH PROBLEMS (trigger: {trigger}): {msg}",
                    trigger, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Interrupted",
                "App was shutting down. If tables were already truncated they are only partly loaded — run the reload again.", 0, 0));
            _logger.LogWarning("Nightly full reload interrupted by shutdown (trigger: {trigger}).", trigger);
        }
        catch (Exception ex)
        {
            SetStatus(new ReloadStatus(false, trigger, startedUtc, DateTime.UtcNow, "Failed", ex.Message, 0, 0));
            _logger.LogError(ex, "Nightly full reload crashed (trigger: {trigger}).", trigger);
        }
        finally
        {
            _gate.Release();
        }

        return true;
    }
}