using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.EntityFrameworkCore;

namespace AutoGeniusSync.BackgroundServices;

public class SyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<SyncHostedService> _logger;

    // IST = UTC+5:30. Fixed offset (not TimeZoneInfo lookup) so this
    // works regardless of OS timezone registration.
    private static readonly TimeSpan IstOffset = new TimeSpan(5, 30, 0);

    private DateTime _lastReloadDateIst = DateTime.MinValue;

    public SyncHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<SyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SyncHostedService starting — nightly insert-only reload mode (12:00 AM IST).");

        while (!ct.IsCancellationRequested)
        {
            var nowIst = DateTime.UtcNow.Add(IstOffset);

            bool isMidnightIst = nowIst.Hour == 0 && nowIst.Minute == 0;

            if (isMidnightIst && _lastReloadDateIst != nowIst.Date)
            {
                _lastReloadDateIst = nowIst.Date;
                _logger.LogInformation("Midnight IST trigger fired at {time} — starting nightly insert-only reload (no truncate).", nowIst);

                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<DataSyncService>();

                try
                {
                    var result = await svc.RunNightlyFullReloadAsync(ct);
                    _logger.LogInformation(
                        "Nightly reload complete: {fet} fetched, {ins} inserted.",
                        result.RecordsFetched, result.RecordsInserted);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Nightly reload failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }
}