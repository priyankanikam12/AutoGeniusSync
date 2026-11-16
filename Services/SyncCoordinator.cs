using Microsoft.Data.SqlClient;

namespace AutoGeniusSync.Services;

// ─────────────────────────────────────────────────────────────
// ONE sync at a time — in this process AND across processes.
//
// Why both: IIS "overlapped recycle" starts a NEW worker process while the OLD one is still finishing,
// and a desktop copy of the app may point at the same database. A static SemaphoreSlim only protects
// one process, so two historical/nightly syncs could run together, double the ERP load (→ HTTP 503)
// and race on the same rows. The database application lock (sp_getapplock, session-owned, on a
// dedicated non-pooled connection) is released automatically by SQL Server if the process dies, so a
// killed worker can never leave a stale lock behind.
//
// Every entry point that writes ERP data (nightly window, full/missing load, historical checkpoint
// sync, manual backfill endpoints) goes through NightlyReloadRunner, which takes this lock first.
// ─────────────────────────────────────────────────────────────
public sealed class SyncCoordinator
{
    private const string LockResource = "AutoGeniusSync.ErpSync";
    private readonly SemaphoreSlim _local = new(1, 1);
    private readonly IConfiguration _config;
    private readonly ILogger<SyncCoordinator> _logger;

    public SyncCoordinator(IConfiguration config, ILogger<SyncCoordinator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsHeldLocally => _local.CurrentCount == 0;

    // Returns null when another sync holds the lock (here or in another process). Never waits.
    public async Task<IAsyncDisposable?> TryAcquireAsync(string owner, CancellationToken ct)
    {
        if (!await _local.WaitAsync(0, ct))
            return null;

        SqlConnection? conn = null;
        try
        {
            var cs = _config.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
                return new Handle(this, null, owner);   // no DB configured: process-local lock only

            var b = new SqlConnectionStringBuilder(cs) { Pooling = false, ApplicationName = "AutoGeniusSync-SyncLock" };
            conn = new SqlConnection(b.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand("sp_getapplock", conn) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", 0);
            var ret = cmd.Parameters.Add("@RETURN_VALUE", System.Data.SqlDbType.Int);
            ret.Direction = System.Data.ParameterDirection.ReturnValue;
            await cmd.ExecuteNonQueryAsync(ct);

            if ((int)ret.Value < 0)
            {
                _logger.LogWarning("Sync '{owner}' not started — another process (e.g. overlapped IIS worker) holds the sync lock.", owner);
                await conn.DisposeAsync();
                _local.Release();
                return null;
            }
            return new Handle(this, conn, owner);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Database unreachable: do not run a sync at all (it would fail on the first write anyway).
            _logger.LogWarning("Sync '{owner}' not started — could not take the database sync lock: {msg}", owner, ex.Message);
            if (conn != null) await conn.DisposeAsync();
            _local.Release();
            return null;
        }
        catch
        {
            if (conn != null) await conn.DisposeAsync();
            _local.Release();
            throw;
        }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly SyncCoordinator _owner;
        private readonly SqlConnection? _conn;
        private readonly string _name;
        private int _disposed;

        public Handle(SyncCoordinator owner, SqlConnection? conn, string name)
        {
            _owner = owner; _conn = conn; _name = name;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try
            {
                if (_conn != null)
                {
                    try
                    {
                        await using var cmd = new SqlCommand("sp_releaseapplock", _conn) { CommandType = System.Data.CommandType.StoredProcedure };
                        cmd.Parameters.AddWithValue("@Resource", LockResource);
                        cmd.Parameters.AddWithValue("@LockOwner", "Session");
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        // Closing the (non-pooled) connection below releases the lock anyway.
                        _owner._logger.LogDebug("sp_releaseapplock for '{name}' failed: {msg}", _name, ex.Message);
                    }
                    await _conn.DisposeAsync();
                }
            }
            finally
            {
                _owner._local.Release();
            }
        }
    }
}
