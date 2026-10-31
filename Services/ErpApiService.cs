//Services\ErpApiService.cs
using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoGeniusSync.Services;

// Result of one read-only ERP probe call (no business data — counts, status and the ERP's own message only).
public sealed record ErpProbeResult(
    int HttpStatus, bool? Valid, string? Description, int Rows, int BodyChars, long Ms, string? TopLevelKeys, string? Error,
    string? RequestJson = null);   // the exact JSON body the app sent (no token, no password) — to compare with Postman

// Thrown when the ERP answers HTTP 200 with Valid=false and a description such as "Unauthorized".
// That is an access problem (wrong/limited token or login), NOT "no data" — it must never be loaded as 0 rows.
public sealed class ErpAuthorizationException : Exception
{
    public ErpAuthorizationException(string message) : base(message) { }
}

public class ErpApiService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErpApiService> _logger;
    private readonly string _baseUrl;
    private readonly int _delayMs;

    // ─────────────────────────────────────────────────────────
    // FIX: application-wide throttle — only ONE ERP HTTP call executes
    // at a time, regardless of how many sync methods, background tasks,
    // or manually-triggered controller endpoints try to call the ERP
    // concurrently (startup backfill sequence, curl-triggered backfills,
    // periodic daily syncs, etc.). This is the root fix for the HTTP 503
    // "service unavailable" and clustered 180s timeouts seen when
    // multiple heavy full-range queries hit the ERP at the same time —
    // a per-method sequential fix inside one caller (e.g.
    // SyncHostedService) can't prevent this, since separate
    // independently-triggered background tasks are not coordinated
    // with each other at all. This semaphore is static so it is shared
    // across every instance/scope of this service in the process.
    // ─────────────────────────────────────────────────────────
    private static readonly SemaphoreSlim _erpCallThrottle = new(1, 1);

    // Number of ERP records that were cut out of a response because they were malformed beyond repair
    // (see DeserializeTolerant). Process-wide; DataSyncService reads the difference before/after a run and
    // prints it in the sync log, so skipped records are never silent.
    // Time of the last forced re-login (avoids a login storm if the ERP keeps refusing).
    private static DateTime _lastForcedRefreshUtc = DateTime.MinValue;

    private static bool LooksLikeAuthRefusal(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var d = description.ToLowerInvariant();
        return d.Contains("unauthor") || d.Contains("forbidden") || d.Contains("access denied")
            || d.Contains("invalid token") || d.Contains("token expired") || d.Contains("not authori");
    }

    private static long _malformedRecordsDropped;
    public static long MalformedRecordsDropped => Interlocked.Read(ref _malformedRecordsDropped);

    public ErpApiService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        ILogger<ErpApiService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _baseUrl = config["AutoGeniusERP:BaseUrl"]!;
        _delayMs = config.GetValue<int>("AutoGeniusERP:ApiDelayMs", 500);
    }

    // ─────────────────────────────────────────────────────────
    // TOKEN MANAGEMENT
    // ─────────────────────────────────────────────────────────

    public async Task<string> GetValidTokenAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var threshold = DateTime.UtcNow.AddMinutes(30);
        var active = await db.DmsAuthTokens
            .Where(t => t.IsActive && t.ExpiresAt > threshold)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (active != null)
            return active.AccessToken;

        _logger.LogInformation("Token expired or missing, refreshing...");
        return await RefreshTokenAsync(db);
    }

    private async Task<string> RefreshTokenAsync(AppDbContext db)
    {
        var loginUrl = _baseUrl + _config["AutoGeniusERP:LoginUrl"];
        var req = new LoginRequest
        {
            Username = _config["AutoGeniusERP:Username"]!,
            Password = _config["AutoGeniusERP:PasswordBase64"]!
        };

        var content = new StringContent(
            JsonConvert.SerializeObject(req),
            System.Text.Encoding.UTF8,
            "application/json");

        using var http = _httpFactory.CreateClient("ErpApi");
        var resp = await http.PostAsync(loginUrl, content);
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ErpApiResponse<LoginValue>>(body)
            ?? throw new Exception("Login response was null");

        if (!result.Valid || result.Value == null || !result.Value.Any())
            throw new Exception($"Login failed: {result.Description}");

        var val = result.Value.First();
        var refreshHours = _config.GetValue<int>("AutoGeniusERP:TokenRefreshHours", 23);

        await db.DmsAuthTokens.Where(t => t.IsActive)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.IsActive, false));

        var token = new DmsAuthToken
        {
            AccessToken = val.AccessToken!,
            LoginEmail  = val.LoginEmail,
            VendorName  = val.VendorName,
            VendorCode  = val.VendorCode,
            VendorId    = val.VendorId,
            CreatedAt   = DateTime.UtcNow,
            ExpiresAt   = DateTime.UtcNow.AddHours(refreshHours),
            IsActive    = true
        };
        db.DmsAuthTokens.Add(token);
        await db.SaveChangesAsync();

        _logger.LogInformation("Token refreshed. Expires at {exp}", token.ExpiresAt);
        return token.AccessToken;
    }

    // ─────────────────────────────────────────────────────────
    // PINCODE / DEALER
    // ─────────────────────────────────────────────────────────

    public async Task<List<DealerValue>> FetchDealersByPinAsync(string pinCode, string token)
    {
        await Task.Delay(_delayMs);
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var url = $"{_baseUrl}/V1/booking/pin?code={pinCode}&Ver=1.0&vendorid={vendorId}";

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildGetRequest(url, token);
        var resp = await http.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ErpApiResponse<DealerValue>>(body);
        return result?.Valid == true ? result.Value ?? new() : new();
    }

    public async Task<string> FetchRawVdrRangeAsync(DateTime fromDate, DateTime toDate, string token)
    {
        await Task.Delay(_delayMs);
        var url = $"{_baseUrl}/V1/erpreport/vdr?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new VdrRequest
        {
            VendorId = vendorId,
            FromDate = fromDate.ToString("dd-MM-yyyy"),
            ToDate   = toDate.ToString("dd-MM-yyyy"),
            DealerCode = "", LocationCode = "", ChassisNo = "", MobileNo = "",
            VhclStatus = "ALL", SubVendorCode = ""
        };

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, req, token);
        var resp  = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return StripBomAndDecode(bytes);
    }

    // Generic debug-parse — works for DJR, VSR, or VDR raw JSON given the type param
    public object DebugParseRawJson<T>(string raw)
    {
        var sanitized = SanitizeJson(raw);

        try
        {
            var result = JsonConvert.DeserializeObject<ErpApiResponse<T>>(sanitized);
            return new { FastPathSucceeded = true, Valid = result?.Valid, ItemCount = result?.Value?.Count ?? 0, RawLength = raw.Length };
        }
        catch (JsonException ex)
        {
            int approxCharPos = 0;
            if (ex is Newtonsoft.Json.JsonReaderException jre)
            {
                var lines = sanitized.Split('\n');
                for (int i = 0; i < jre.LineNumber - 1 && i < lines.Length; i++)
                    approxCharPos += lines[i].Length + 1;
                approxCharPos += jre.LinePosition;
            }
            var start = Math.Max(0, approxCharPos - 200);
            var len   = Math.Min(400, sanitized.Length - start);
            return new
            {
                FastPathSucceeded = false,
                ExceptionMessage  = ex.Message,
                ApproxCharPosition = approxCharPos,
                RawLength = raw.Length,
                ContextAroundError = sanitized.Substring(start, len)
            };
        }
    }

    public async Task<string> FetchRawVsrRangeAsync(DateTime fromDate, DateTime toDate, string token)
    {
        await Task.Delay(_delayMs);
        var url = $"{_baseUrl}/V1/erpreport/vsr?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var req = new VsrRequest
        {
            DealerCode = "", VendorId = vendorId,
            StartDate = fromDate.ToString("dd-MM-yyyy"), EndDate = toDate.ToString("dd-MM-yyyy"),
            SubVendorCode = "", DealerStatus = "1", AadharPanReq = "0", FameReq = "2"
        };
        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, req, token);
        var resp  = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return StripBomAndDecode(bytes);
    }

    // ─────────────────────────────────────────────────────────
    // DAILY JOB REPORT (DJR)
    // ─────────────────────────────────────────────────────────

    public async Task<List<DjrValue>> FetchDjrAsync(
        DateTime date, string token, string dealerCode = "")
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var dateStr  = date.ToString("dd-MM-yyyy");

        var req = new DjrRequest
        {
            VendorId   = vendorId,
            StartDate  = dateStr,
            EndDate    = dateStr,
            DealerCode = dealerCode
        };

        return await PostWithRetryAsync<DjrValue>(url, req, token, maxRetries: 3);
    }

    // ─────────────────────────────────────────────────────────
    // LINE ORDER REPORT (LOR)
    // ─────────────────────────────────────────────────────────
    public async Task<List<LorValue>> FetchLorAsync(
        string dealerCode, DateTime startDate, DateTime endDate, string token)
    {
        await Task.Delay(_delayMs);
        var url      = $"{_baseUrl}/V1/erpreport/lor?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new LorRequest
        {
            VendorId   = vendorId,
            StartDate  = startDate.ToString("dd-MM-yyyy"),
            EndDate    = endDate.ToString("dd-MM-yyyy"),
            DealerCode = dealerCode
        };

        _logger.LogInformation(
            "Fetching LOR for dealer {dc} | {from} → {to}",
            dealerCode,
            startDate.ToString("dd-MM-yyyy"),
            endDate.ToString("dd-MM-yyyy"));

        return await PostWithRetryAsync<LorValue>(url, req, token, maxRetries: 3);
    }

    public async Task<string> FetchRawLorAsync(
        string dealerCode, DateTime date, string token)
    {
        await Task.Delay(_delayMs);
        var url      = $"{_baseUrl}/V1/erpreport/lor?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new LorRequest
        {
            VendorId   = vendorId,
            StartDate  = date.ToString("dd-MM-yyyy"),
            EndDate    = date.ToString("dd-MM-yyyy"),
            DealerCode = dealerCode
        };

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, req, token);
        var resp = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return StripBomAndDecode(bytes);
    }

    // ─────────────────────────────────────────────────────────
    // DJR — WIDE RANGE, ALL DEALERS
    // ─────────────────────────────────────────────────────────

    public async Task<List<DjrValue>> FetchDjrRangeAsync(DateTime startDate, DateTime endDate, string token)
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new DjrRequest
        {
            VendorId   = vendorId,
            StartDate  = startDate.ToString("dd-MM-yyyy"),
            EndDate    = endDate.ToString("dd-MM-yyyy"),
            DealerCode = ""
        };

        _logger.LogInformation("Fetching DJR range {from} → {to} (all dealers, one call)",
            startDate.ToString("dd-MM-yyyy"), endDate.ToString("dd-MM-yyyy"));

        return await PostWithRetryAsync<DjrValue>(url, req, token, maxRetries: 3);
    }

    public async Task<string> FetchRawDjrRangeAsync(DateTime startDate, DateTime endDate, string token)
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new DjrRequest
        {
            VendorId   = vendorId,
            StartDate  = startDate.ToString("dd-MM-yyyy"),
            EndDate    = endDate.ToString("dd-MM-yyyy"),
            DealerCode = ""
        };

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, req, token);
        var resp  = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return StripBomAndDecode(bytes);
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE SALES REPORT (VSR)
    // ─────────────────────────────────────────────────────────

    public async Task<List<VsrValue>> FetchVsrAsync(
        string dealerCode, DateTime startDate, DateTime endDate, string token)
    {
        await Task.Delay(_delayMs);
        var url      = $"{_baseUrl}/V1/erpreport/vsr?ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new VsrRequest
        {
            DealerCode    = dealerCode,
            VendorId      = vendorId,
            StartDate     = startDate.ToString("dd-MM-yyyy"),
            EndDate       = endDate.ToString("dd-MM-yyyy"),
            SubVendorCode = "",
            DealerStatus  = "1",
            AadharPanReq  = "0",
            FameReq       = "2"
        };

        return await PostWithRetryAsync<VsrValue>(url, req, token, maxRetries: 3);
    }

    public async Task<object> DebugDjrRangeParseAsync(DateTime startDate, DateTime endDate, string token)
    {
        var raw = await FetchRawDjrRangeAsync(startDate, endDate, token);
        return DebugParseRawJson<DjrValue>(raw);
    }

    // ─────────────────────────────────────────────────────────
    // FETCH RAW DJRN JSON — debug endpoint only
    // ─────────────────────────────────────────────────────────

    public async Task<string> FetchRawDjrnAsync(
        DateTime date, string token, string chassisNo = "", string dealerCode = "")
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrnUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var dateStr  = date.ToString("dd-MM-yyyy");

        var bodyObj = new
        {
            vendorid   = vendorId,
            startdate  = dateStr,
            enddate    = dateStr,
            dealercode = dealerCode,
            chassisno  = chassisNo
        };

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, bodyObj, token);
        var resp  = await http.SendAsync(request);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return StripBomAndDecode(bytes);
    }

    public async Task<List<DjrValue>> FetchDjrnAsync(
        DateTime date, string token, string chassisNo = "", string dealerCode = "")
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrnUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var dateStr  = date.ToString("dd-MM-yyyy");

        var bodyObj = new
        {
            vendorid   = vendorId,
            startdate  = dateStr,
            enddate    = dateStr,
            dealercode = dealerCode,
            chassisno  = chassisNo
        };

        return await PostWithRetryAsync<DjrValue>(url, bodyObj, token, maxRetries: 3);
    }

    // ─────────────────────────────────────────────────────────
    // VEHICLE DISPATCH REPORT (VDR)
    // ─────────────────────────────────────────────────────────

    public async Task<List<VdrValue>> FetchVdrAsync(
        DateTime fromDate, DateTime toDate, string token,
        string dealerCode = "", string vhclStatus = "ALL")
    {
        await Task.Delay(_delayMs);
        var url      = $"{_baseUrl}/V1/erpreport/vdr?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new VdrRequest
        {
            VendorId      = vendorId,
            FromDate      = fromDate.ToString("dd-MM-yyyy"),
            ToDate        = toDate.ToString("dd-MM-yyyy"),
            DealerCode    = dealerCode,
            LocationCode  = "",
            ChassisNo     = "",
            MobileNo      = "",
            VhclStatus    = vhclStatus,
            SubVendorCode = ""
        };

        return await PostWithRetryAsync<VdrValue>(url, req, token, maxRetries: 3);
    }

    // ─────────────────────────────────────────────────────────
    // CALL CENTRE DEALER BY PINCODE
    // ─────────────────────────────────────────────────────────

    public async Task<List<CallCentreDealerValue>> FetchCallCentreDealersByPinAsync(
        string pinCode, string token)
    {
        await Task.Delay(_delayMs);
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var url = $"{_baseUrl}/V1/callcenter/pin?code={pinCode}&Ver=1.0&vendorid={vendorId}";

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildGetRequest(url, token);
        var resp = await http.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ErpApiResponse<CallCentreDealerValue>>(body);
        return result?.Valid == true ? result.Value ?? new() : new();
    }

    public async Task<string> FetchRawDjrAsync(DateTime date, string token)
    {
        await Task.Delay(_delayMs);
        var url      = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new DjrRequest
        {
            VendorId   = vendorId,
            StartDate  = date.ToString("dd-MM-yyyy"),
            EndDate    = date.ToString("dd-MM-yyyy"),
            DealerCode = ""
        };

        using var http    = _httpFactory.CreateClient("ErpApi");
        using var request = BuildPostRequest(url, req, token);
        var resp = await http.SendAsync(request);
        return await resp.Content.ReadAsStringAsync();
    }

    // Deactivates every stored token and logs in again with THIS server's own configured ERP credentials.
    // Used when the ERP answers "Unauthorized": the stored token may have been created by another
    // instance (e.g. a desktop copy with different credentials) that shares the same database.
    private async Task<string> ForceRefreshTokenAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await RefreshTokenAsync(db);
    }

    // ─────────────────────────────────────────────────────────
    // READ-ONLY PROBE — one call, no retry, nothing stored.
    // Returns what the ERP actually answered: HTTP status, Valid flag, the ERP's own Description
    // text, the number of rows, size of the body and the names of the top-level JSON fields.
    // Used by DataSyncService.ProbeErpReportsAsync (POST api/admin/reload/probe).
    // report: djr | vsr | vdr | lor
    // ─────────────────────────────────────────────────────────
    public async Task<ErpProbeResult> ProbeReportAsync(
        string report, string dealerCode, DateTime from, DateTime to, string token)
    {
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var f = from.ToString("dd-MM-yyyy");
        var t = to.ToString("dd-MM-yyyy");

        string url;
        object req;
        switch (report.ToLowerInvariant())
        {
            case "djr":
                url = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
                req = new DjrRequest { VendorId = vendorId, StartDate = f, EndDate = t, DealerCode = dealerCode };
                break;
            case "vsr":
                url = $"{_baseUrl}/V1/erpreport/vsr?ver=1.0";
                req = new VsrRequest
                {
                    DealerCode = dealerCode, VendorId = vendorId, StartDate = f, EndDate = t,
                    SubVendorCode = "", DealerStatus = "1", AadharPanReq = "0", FameReq = "2"
                };
                break;
            case "vdr":
                url = $"{_baseUrl}/V1/erpreport/vdr?Ver=1.0";
                req = new VdrRequest
                {
                    VendorId = vendorId, FromDate = f, ToDate = t, DealerCode = dealerCode,
                    LocationCode = "", ChassisNo = "", MobileNo = "", VhclStatus = "ALL", SubVendorCode = ""
                };
                break;
            case "lor":
                url = $"{_baseUrl}/V1/erpreport/lor?Ver=1.0";
                req = new LorRequest { VendorId = vendorId, StartDate = f, EndDate = t, DealerCode = dealerCode };
                break;
            default:
                throw new ArgumentException($"Unknown report '{report}'. Use djr, vsr, vdr, lor.");
        }

        await Task.Delay(_delayMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var requestJson = JsonConvert.SerializeObject(req);
        await _erpCallThrottle.WaitAsync();
        try
        {
            var timeoutSeconds = _config.GetValue<int>("AutoGeniusERP:HttpTimeoutSeconds", 120);
            using var http    = _httpFactory.CreateClient("ErpApi");
            using var request = BuildPostRequest(url, req, token);
            using var cts     = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var resp   = await http.SendAsync(request, cts.Token);
            var bytes  = await resp.Content.ReadAsByteArrayAsync();
            var body   = StripBomAndDecode(bytes);
            var status = (int)resp.StatusCode;

            try
            {
                var root  = JObject.Parse(SanitizeJson(body));
                bool? valid = root["Valid"]?.Type == JTokenType.Boolean ? root["Valid"]!.Value<bool>() : null;
                var desc  = (root["Description"] ?? root["description"] ?? root["Message"] ?? root["message"])?.ToString();
                var rows  = (root["Value"] as JArray)?.Count ?? 0;
                var keys  = string.Join(",", root.Properties().Select(p => p.Name));
                return new ErpProbeResult(status, valid, desc == null ? null : Trunc(desc, 200),
                                          rows, body.Length, sw.ElapsedMilliseconds, keys, null, requestJson);
            }
            catch (Exception pex)
            {
                return new ErpProbeResult(status, null, null, 0, body.Length, sw.ElapsedMilliseconds, null,
                                          "response is not parseable JSON: " + Trunc(pex.Message, 200), requestJson);
            }
        }
        catch (OperationCanceledException)
        {
            return new ErpProbeResult(0, null, null, 0, 0, sw.ElapsedMilliseconds, null, "timed out", requestJson);
        }
        catch (Exception ex)
        {
            return new ErpProbeResult(0, null, null, 0, 0, sw.ElapsedMilliseconds, null, Trunc(ex.Message, 200), requestJson);
        }
        finally
        {
            _erpCallThrottle.Release();
        }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    // ─────────────────────────────────────────────────────────
    // SHARED: POST with retry
    //
    // FIX (critical, earlier): swallowed failures now throw instead of
    // returning an empty list — see DeserializeTolerant.
    //
    // FIX (this pass): wrapped the ENTIRE method body in the global
    // _erpCallThrottle semaphore so only one ERP HTTP call executes at
    // a time across the whole app, no matter which sync method or
    // background task/controller endpoint triggered it. Also added a
    // longer, 503-specific backoff — an HTTP 503 means the ERP itself
    // is telling us it's overloaded, so retrying quickly is likely to
    // hit the same wall; back off harder (30s * attempt) than a normal
    // failure (10s * attempt).
    // ─────────────────────────────────────────────────────────

    private async Task<List<T>> PostWithRetryAsync<T>(
        string url, object requestBody, string token, int maxRetries = 3)
    {
        await _erpCallThrottle.WaitAsync();
        try
        {
            var timeoutSeconds = _config.GetValue<int>("AutoGeniusERP:HttpTimeoutSeconds", 120);
            Exception? lastException = null;
            var tokenRefreshed = false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var http    = _httpFactory.CreateClient("ErpApi");
                    using var request = BuildPostRequest(url, requestBody, token);
                    using var cts     = new CancellationTokenSource(
                                            TimeSpan.FromSeconds(timeoutSeconds));

                    _logger.LogInformation(
                        "Attempt {attempt}/{max} → POST {url} (timeout: {t}s)",
                        attempt, maxRetries, url, timeoutSeconds);

                    var resp = await http.SendAsync(request, cts.Token);

                    var rawBytes = await resp.Content.ReadAsByteArrayAsync();
                    var body     = StripBomAndDecode(rawBytes);

                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Attempt {attempt}/{max} got HTTP {code} from {url}",
                            attempt, maxRetries, (int)resp.StatusCode, url);

                        lastException = new Exception(
                            $"HTTP {(int)resp.StatusCode}: " +
                            $"{body[..Math.Min(200, body.Length)]}");

                        if (attempt < maxRetries)
                        {
                            // FIX: back off harder specifically on 503 —
                            // the ERP is explicitly telling us it's overloaded.
                            var delaySeconds = (int)resp.StatusCode == 503
                                ? attempt * 30
                                : attempt * 10;

                            _logger.LogWarning(
                                "Backing off {delay}s before retry (status {code})",
                                delaySeconds, (int)resp.StatusCode);

                            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                            continue;
                        }
                        break;
                    }

                    return DeserializeTolerant<T>(body, url, attempt, maxRetries);
                }
                catch (OperationCanceledException)
                {
                    lastException = new Exception(
                        $"Request timed out after {timeoutSeconds}s");

                    _logger.LogWarning(
                        "Attempt {attempt}/{max} timed out for {url}. {rem} attempt(s) left.",
                        attempt, maxRetries, url, maxRetries - attempt);

                    if (attempt < maxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(
                        "Attempt {attempt}/{max} network error for {url}: {msg}",
                        attempt, maxRetries, url, ex.Message);

                    if (attempt < maxRetries)
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
                }
                catch (JsonException)
                {
                    // Bubble parse failures straight up — no point retrying
                    // identical malformed content from the same request.
                    throw;
                }
                catch (ErpAuthorizationException authEx)
                {
                    // "Unauthorized": try ONCE with a fresh login from this server's own credentials
                    // (the stored token may belong to another instance/login). Not counted as a retry.
                    if (!tokenRefreshed && DateTime.UtcNow - _lastForcedRefreshUtc > TimeSpan.FromMinutes(2))
                    {
                        tokenRefreshed = true;
                        _lastForcedRefreshUtc = DateTime.UtcNow;
                        _logger.LogWarning(
                            "{msg} — refreshing the ERP token with this server's own credentials and retrying once.",
                            authEx.Message);
                        token = await ForceRefreshTokenAsync();
                        attempt--;
                        continue;
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Attempt {attempt}/{max} unexpected error for {url}",
                        attempt, maxRetries, url);
                    throw;
                }
            }

            // FIX: throw instead of "return new()". A genuine empty-but-valid
            // API response (Valid=true, Value=[]) already returns cleanly from
            // DeserializeTolerant above and never reaches this line — so
            // reaching here means every attempt genuinely failed.
            throw new Exception(
                $"All {maxRetries} attempts failed for {url}. Last error: {lastException?.Message}");
        }
        finally
        {
            // FIX: always release, even on exception, so the next queued
            // ERP call (from any other sync method or endpoint) can proceed.
            _erpCallThrottle.Release();
        }
    }

    // ─────────────────────────────────────────────────────────
    // BOM STRIPPER
    // ─────────────────────────────────────────────────────────

    private static string StripBomAndDecode(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    // ─────────────────────────────────────────────────────────
    // JSON SANITIZER
    // ─────────────────────────────────────────────────────────

    private static string SanitizeJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // ── Pass -1: Fix invalid JSON escape sequences (e.g. \H) ──────────
        // FIX: the ERP occasionally emits a raw backslash followed by a
        // character that isn't a valid JSON escape (only " \ / b f n r t u
        // are valid) — usually inside the "Description" field (e.g. a path,
        // dealer name, or note containing a literal backslash). This was
        // killing the ENTIRE response with "Bad JSON escape sequence: \H"
        // even when the rest of the payload (potentially tens of thousands
        // of good records) was perfectly fine. Must run BEFORE
        // FixUnescapedQuotes below, since an already-broken escape could
        // otherwise be misread as a quote terminator.
        raw = FixInvalidEscapes(raw);

        // ── Pass 0: Fix embedded/unescaped quotes inside string VALUES ────
        // FIX: the ERP sometimes emits values containing a raw " character,
        // e.g. "IndividualAHBattery1":"12"AH" — the parser treats the quote
        // after "12" as the string terminator and then chokes on the
        // trailing AH". This was throwing out entire multi-year fetches
        // (DJR/VSR/VDR/LOR) because ONE bad row anywhere in the payload
        // failed the whole parse. See FixUnescapedQuotes for the rules.
        raw = FixUnescapedQuotes(raw);

        // ── Pass 1: Remove literal control characters inside JSON strings ──
        // The ERP API embeds raw \r \n \t and other control chars (0x00-0x1F)
        // directly inside string values, which breaks the JSON parser.
        // We scan char by char and strip them when inside a string literal.
        var sb = new System.Text.StringBuilder(raw.Length);
        bool inString = false;
        bool escaped  = false;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escaped = true;
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                sb.Append(c);
                continue;
            }

            // Drop bare control characters inside strings (0x00–0x1F except
            // legitimate JSON whitespace outside strings)
            if (inString && c < 0x20)
            {
                // Replace with a space so we don't corrupt adjacent words
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
        }

        raw = sb.ToString();

        // ── Pass 2: Fix spaced JSON keys (existing logic) ─────────────────
        // e.g. "Dealer Code" → already handled by [JsonProperty("Dealer Code")]
        // but some keys have extra whitespace around the colon
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"""(\w[\w\s]*?)""\s*:", m => $"\"{m.Groups[1].Value.Trim()}\":");

        // ── Pass 3: Fix IndividualAH Battery → IndividualAHBattery ────────
        raw = raw.Replace("IndividualAH Battery", "IndividualAHBattery");

        return raw;
    }

    // ─────────────────────────────────────────────────────────
    // FIX: repair invalid JSON escape sequences like \H, \S, etc. by
    // escaping the stray backslash itself, so a single malformed field
    // doesn't invalidate the whole payload.
    // ─────────────────────────────────────────────────────────
    private static string FixInvalidEscapes(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length + 32);
        bool inString = false;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }

            if (c == '"')
            {
                inString = false;
                sb.Append(c);
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                char next = raw[i + 1];
                bool isValidEscape =
                    next == '"' || next == '\\' || next == '/' ||
                    next == 'b' || next == 'f' || next == 'n' ||
                    next == 'r' || next == 't' || next == 'u';

                if (isValidEscape)
                {
                    sb.Append(c);
                    sb.Append(next);
                    i++; // consume both characters
                }
                else
                {
                    // Invalid escape (e.g. \H) — treat the backslash as
                    // literal by escaping it, so \H becomes \\H.
                    sb.Append("\\\\");
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────
    // FIX: escape stray embedded quotes inside JSON string values
    // so a single malformed field doesn't corrupt the entire payload.
    //
    // Rules (context-aware — the previous version treated a quote followed by ':' as a closing quote
    // for BOTH keys and values, so a value such as  FS "2": COUPON  ended the string early and the
    // parser failed with "After parsing a value an unexpected character was encountered: :"):
    //   • a KEY string closes only at a quote followed by ':'
    //   • a VALUE string closes only at a quote followed by '}' or ']', or by ',' that is followed by a
    //     new key ("...) in an object (any value in an array)
    //   • every other quote inside a string is embedded content and gets escaped (\")
    // Valid JSON is left byte-for-byte unchanged (checked with 35,000 random documents).
    // ─────────────────────────────────────────────────────────
    private static string FixUnescapedQuotes(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length + 32);
        var containers = new Stack<char>();   // 'o' = object, 'a' = array
        char lastSig = '\0';                  // last non-whitespace char seen outside strings
        bool inString = false;
        bool escaped  = false;
        bool isKey    = false;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"')
                {
                    inString = true;
                    bool inArray = containers.Count > 0 && containers.Peek() == 'a';
                    isKey = !inArray && lastSig != ':';
                    continue;
                }
                if (!char.IsWhiteSpace(c))
                {
                    if (c == '{') containers.Push('o');
                    else if (c == '[') containers.Push('a');
                    else if ((c == '}' || c == ']') && containers.Count > 0) containers.Pop();
                    lastSig = c;
                }
                continue;
            }

            if (escaped)
            {
                sb.Append(c);
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                sb.Append(c);
                continue;
            }

            if (c == '"')
            {
                int j = i + 1;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;

                bool close;
                if (j >= raw.Length)
                {
                    close = true;
                }
                else if (isKey)
                {
                    close = raw[j] == ':';
                }
                else
                {
                    char n = raw[j];
                    if (n == '}' || n == ']')
                    {
                        close = true;
                    }
                    else if (n == ',')
                    {
                        int k = j + 1;
                        while (k < raw.Length && char.IsWhiteSpace(raw[k])) k++;
                        bool inArr = containers.Count > 0 && containers.Peek() == 'a';
                        close = k >= raw.Length || inArr || raw[k] == '"';
                    }
                    else
                    {
                        close = false;
                    }
                }

                if (close)
                {
                    inString = false;
                    lastSig  = '"';
                    sb.Append(c);
                }
                else
                {
                    sb.Append("\\\"");
                }
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────
    // LAST-RESORT SALVAGE: cut ONE malformed record out of the response.
    // If, even after sanitising, the JSON parser fails inside a record, a single bad row used to make
    // the whole call (a month of DJR, thousands of good rows) fail. This locates the record that
    // contains the parser's error position, removes just that record (between the nearest "},{"
    // boundaries) and lets the caller parse the rest. Every cut is counted
    // (MalformedRecordsDropped), logged and shown in the sync-log note — never silent.
    // Configurable: AutoGeniusERP:MaxMalformedRecordsPerCall (default 50, 0 = off).
    // ─────────────────────────────────────────────────────────
    private static int IndexFromLinePosition(string s, int line, int pos)
    {
        int idx = 0, cur = 1;
        while (cur < line)
        {
            int nl = s.IndexOf('\n', idx);
            if (nl < 0) return -1;
            idx = nl + 1;
            cur++;
        }
        idx += Math.Max(0, pos - 1);
        return idx < s.Length ? idx : -1;
    }

    internal static bool TryCutRecordAt(ref string body, int idx, out int removedChars)
    {
        removedChars = 0;
        if (idx < 0 || idx >= body.Length) return false;

        int sepBefore = body.LastIndexOf("},{", Math.Min(idx + 2, body.Length - 1), StringComparison.Ordinal);
        int arrBefore = body.LastIndexOf("[{",  Math.Min(idx + 1, body.Length - 1), StringComparison.Ordinal);
        if (sepBefore < 0 && arrBefore < 0) return false;

        int recStart;      // index of the record's opening '{'
        int cutFrom;       // first char to remove
        bool hasCommaBefore = sepBefore > arrBefore;
        if (hasCommaBefore) { recStart = sepBefore + 2; cutFrom = recStart; }
        else                { recStart = arrBefore + 1; cutFrom = recStart; }

        int nextSep = body.IndexOf("},{", Math.Max(idx, recStart), StringComparison.Ordinal);
        int arrEnd  = body.IndexOf("}]",  Math.Max(idx, recStart), StringComparison.Ordinal);

        int cutTo;         // first char to keep after the cut
        if (nextSep >= 0 && (arrEnd < 0 || nextSep < arrEnd))
        {
            cutTo = nextSep + 2;                      // removes "{BAD}," and keeps the next "{"
        }
        else if (arrEnd >= 0)
        {
            cutTo = arrEnd + 1;                       // removes "{BAD}" and keeps "]"
            if (hasCommaBefore) cutFrom = recStart - 1;   // …and the comma before it
        }
        else return false;

        if (cutTo <= cutFrom) return false;

        removedChars = cutTo - cutFrom;
        body = string.Concat(body.AsSpan(0, cutFrom), body.AsSpan(cutTo));
        return true;
    }

    // ─────────────────────────────────────────────────────────
    // TOLERANT JSON DESERIALIZER
    //
    // FIX: the final catch used to log and return an empty list.
    // Now it throws — so a genuinely malformed/unparseable response
    // (distinct from "Valid=false" or "Value=[]", both of which
    // return cleanly above) surfaces as a real failure to the caller.
    //
    // FIX (this pass): when the ERP answers Valid=false its own Description is now LOGGED (it used to be
    // dropped silently in the fast path, so an ERP-side refusal looked like "no data"); and a response
    // that stays unparseable after sanitising has the offending record cut out instead of failing the
    // whole call (see TryCutRecordAt).
    // ─────────────────────────────────────────────────────────

    private List<T> DeserializeTolerant<T>(
        string body, string url, int attempt, int maxRetries)
    {
        body = SanitizeJson(body);

        var maxCuts = Math.Max(0, _config.GetValue<int>("AutoGeniusERP:MaxMalformedRecordsPerCall", 50));
        var cuts = 0;

        while (true)
        {
            try
            {
                var fastSettings = new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Ignore
                };

                var result = JsonConvert.DeserializeObject<ErpApiResponse<T>>(body, fastSettings);

                if (cuts > 0)
                {
                    Interlocked.Add(ref _malformedRecordsDropped, cuts);
                    _logger.LogWarning(
                        "{n} malformed ERP record(s) were skipped for {url}; the remaining rows were loaded.", cuts, url);
                }

                if (result?.Valid != true)
                {
                    var desc = Trunc(result?.Description ?? "(no description)", 200);
                    _logger.LogWarning("ERP replied Valid=false for {url}: {desc}", url, desc);
                    if (LooksLikeAuthRefusal(result?.Description))
                        throw new ErpAuthorizationException($"ERP authorization refused ('{desc}') for {url}");
                    return new();
                }

                return result.Value ?? new();
            }
            catch (JsonReaderException jre)
            {
                if (cuts < maxCuts)
                {
                    var idx = IndexFromLinePosition(body, jre.LineNumber, jre.LinePosition);
                    if (TryCutRecordAt(ref body, idx, out var removed))
                    {
                        cuts++;
                        _logger.LogWarning(
                            "Malformed ERP record at {path} (line {line}, pos {pos}) for {url}: {msg}. Cut out {chars} chars, retrying parse.",
                            jre.Path, jre.LineNumber, jre.LinePosition, url, jre.Message, removed);
                        continue;
                    }
                }

                _logger.LogWarning(
                    "Fast parse failed for {url}: {msg}. Trying tolerant parse.",
                    url, jre.Message);
                break;
            }
            catch (JsonException firstEx)
            {
                _logger.LogWarning(
                    "Fast parse failed for {url}: {msg}. Trying tolerant parse.",
                    url, firstEx.Message);
                break;
            }
        }

        try
        {
            var root = JObject.Parse(body);

            var valid = root["Valid"]?.Value<bool>() ?? false;
            if (!valid)
            {
                var tdesc = Trunc((root["Description"] ?? root["description"])?.ToString() ?? "(no description)", 200);
                _logger.LogWarning("Tolerant parse: API returned Valid=false for {url}: {desc}", url, tdesc);
                if (LooksLikeAuthRefusal(tdesc))
                    throw new ErpAuthorizationException($"ERP authorization refused ('{tdesc}') for {url}");
                return new();
            }

            var valueToken = root["Value"];
            if (valueToken == null || valueToken.Type == JTokenType.Null)
                return new();

            var items = new List<T>();

            var tolerantSettings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore,
                Error = (_, args) =>
                {
                    _logger.LogDebug(
                        "Tolerant parse skipped field: {path} — {msg}",
                        args.ErrorContext.Path,
                        args.ErrorContext.Error.Message);
                    args.ErrorContext.Handled = true;
                }
            };

            var serializer = JsonSerializer.Create(tolerantSettings);

            foreach (var token in valueToken)
            {
                try
                {
                    var item = token.ToObject<T>(serializer);
                    if (item != null)
                        items.Add(item);
                }
                catch (Exception itemEx)
                {
                    _logger.LogDebug(
                        "Tolerant parse: skipped one item at {url}: {msg}",
                        url, itemEx.Message);
                }
            }

            _logger.LogInformation(
                "Tolerant parse recovered {n} items from {url}", items.Count, url);

            return items;
        }
        catch (ErpAuthorizationException)
        {
            throw;   // keep the type so PostWithRetryAsync can re-login once
        }
        catch (Exception ex)
        {
            // FIX: throw instead of silently returning empty.
            _logger.LogError(ex, "Tolerant parse also failed for {url}.", url);
            throw new Exception($"JSON deserialization failed for {url}: {ex.Message}", ex);
        }
    }

    // ─────────────────────────────────────────────────────────
    // REQUEST BUILDERS
    // ─────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildPostRequest(
        string url, object body, string token)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonConvert.SerializeObject(body),
                System.Text.Encoding.UTF8,
                "application/json")
        };
        msg.Headers.TryAddWithoutValidation("Authorization", $"Token {token}");
        return msg;
    }

    private static HttpRequestMessage BuildGetRequest(string url, string token)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.TryAddWithoutValidation("Authorization", $"Token {token}");
        return msg;
    }
}