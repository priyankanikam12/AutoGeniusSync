using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace AutoGeniusSync.Services;

public class ErpApiService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErpApiService> _logger;
    private readonly string _baseUrl;
    private readonly int _delayMs;

    public ErpApiService(
        HttpClient http,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        ILogger<ErpApiService> logger)
    {
        _http = http;
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

        var resp = await _http.PostAsync(loginUrl, content);
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

        SetAuthHeader(token);
        var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ErpApiResponse<DealerValue>>(body);
        return result?.Valid == true ? result.Value ?? new() : new();
    }

    // ─────────────────────────────────────────────────────────
    // DAILY JOB REPORT (DJR) — with retry on timeout
    // ─────────────────────────────────────────────────────────

    public async Task<List<DjrValue>> FetchDjrAsync(
        DateTime date, string token, string dealerCode = "")
    {
        await Task.Delay(_delayMs);
        var url = _baseUrl + _config["AutoGeniusERP:DjrUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var dateStr = date.ToString("dd-MM-yyyy");

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
    // VEHICLE SALES REPORT (VSR) — with retry on timeout
    // ─────────────────────────────────────────────────────────

    public async Task<List<VsrValue>> FetchVsrAsync(
        string dealerCode, DateTime startDate, DateTime endDate, string token)
    {
        await Task.Delay(_delayMs);
        var url = $"{_baseUrl}/V1/erpreport/vsr?ver=1.0";
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

    // ─────────────────────────────────────────────────────────
    // DJRN — with retry
    // ─────────────────────────────────────────────────────────

    public async Task<List<DjrValue>> FetchDjrnAsync(
        DateTime date, string token, string chassisNo = "", string dealerCode = "")
    {
        await Task.Delay(_delayMs);
        var url = _baseUrl + _config["AutoGeniusERP:DjrnUrl"];
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);
        var dateStr = date.ToString("dd-MM-yyyy");

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
    // VEHICLE DISPATCH REPORT (VDR) — date range, all dealers
    // ─────────────────────────────────────────────────────────
    public async Task<List<VdrValue>> FetchVdrAsync(
        DateTime fromDate, DateTime toDate, string token,
        string dealerCode = "", string vhclStatus = "ALL")
    {
        await Task.Delay(_delayMs);
        var url = $"{_baseUrl}/V1/erpreport/vdr?Ver=1.0";
        var vendorId = _config.GetValue<int>("AutoGeniusERP:VendorId", 14);

        var req = new VdrRequest
        {
            VendorId     = vendorId,
            FromDate     = fromDate.ToString("dd-MM-yyyy"),
            ToDate       = toDate.ToString("dd-MM-yyyy"),
            DealerCode   = dealerCode,
            LocationCode = "",
            ChassisNo    = "",
            MobileNo     = "",
            VhclStatus   = vhclStatus,
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

        SetAuthHeader(token);
        var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ErpApiResponse<CallCentreDealerValue>>(body);
        return result?.Valid == true ? result.Value ?? new() : new();
    }

    // ─────────────────────────────────────────────────────────
    // SHARED: POST with retry on timeout
    // ─────────────────────────────────────────────────────────

    private async Task<List<T>> PostWithRetryAsync<T>(
        string url, object requestBody, string token, int maxRetries = 3)
    {
        var timeoutSeconds = _config.GetValue<int>("AutoGeniusERP:HttpTimeoutSeconds", 120);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                SetAuthHeader(token);

                var content = new StringContent(
                    JsonConvert.SerializeObject(requestBody),
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                _logger.LogInformation("Attempt {attempt}/{max} → POST {url} (timeout: {t}s)",
                    attempt, maxRetries, url, timeoutSeconds);

                var resp = await _http.PostAsync(url, content, cts.Token);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Attempt {attempt}/{max} got HTTP {code} from {url}",
                        attempt, maxRetries, (int)resp.StatusCode, url);
                    lastException = new Exception($"HTTP {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 10)); // 10s, 20s backoff
                        continue;
                    }
                    break;
                }

                var result = JsonConvert.DeserializeObject<ErpApiResponse<T>>(body);
                return result?.Valid == true ? result.Value ?? new() : new();
            }
            catch (OperationCanceledException)
            {
                lastException = new Exception($"Request timed out after {timeoutSeconds}s");
                _logger.LogWarning("Attempt {attempt}/{max} timed out for {url}. {remaining} attempt(s) left.",
                    attempt, maxRetries, url, maxRetries - attempt);

                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 10)); // 10s, 20s backoff
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogWarning("Attempt {attempt}/{max} network error for {url}: {msg}",
                    attempt, maxRetries, url, ex.Message);

                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
            }
            catch (Exception ex)
            {
                // Non-retryable — bail immediately
                _logger.LogError(ex, "Attempt {attempt}/{max} unexpected error for {url}", attempt, maxRetries, url);
                throw;
            }
        }

        _logger.LogWarning("All {max} attempts failed for {url}. Last error: {err}. Returning empty.",
            maxRetries, url, lastException?.Message);
        return new();
    }

    private void SetAuthHeader(string token)
    {
        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.Add("Authorization", $"Token {token}");
    }
}