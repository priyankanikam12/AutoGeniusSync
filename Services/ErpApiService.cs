using AutoGeniusSync.Data;
using AutoGeniusSync.DTOs;
using AutoGeniusSync.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoGeniusSync.Services;

public class ErpApiService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErpApiService> _logger;
    private readonly string _baseUrl;
    private readonly int _delayMs;

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
    // Iterates per-dealer since the API requires dealercode.
    // date range: startDate → endDate (pass same date for daily sync)
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
            DealerCode = dealerCode   // ← from DMS_Dealers.DealerCode
        };

        _logger.LogInformation(
            "Fetching LOR for dealer {dc} | {from} → {to}",
            dealerCode,
            startDate.ToString("dd-MM-yyyy"),
            endDate.ToString("dd-MM-yyyy"));

        return await PostWithRetryAsync<LorValue>(url, req, token, maxRetries: 3);
    }

    //────────────────────────────────────────────────────
    // Fetch Raw LOR JSON — debug endpoint only
    // ─────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────
    // DJRN
    // ─────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────
    // FETCH RAW JSON — debug endpoint only
    // Returns the raw response string before any deserialization.
    // Use GET /api/sync/service-history/debug-raw/{date} to call this.
    // ─────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────
    // SHARED: POST with retry
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
                using var http    = _httpFactory.CreateClient("ErpApi");
                using var request = BuildPostRequest(url, requestBody, token);
                using var cts     = new CancellationTokenSource(
                                        TimeSpan.FromSeconds(timeoutSeconds));

                _logger.LogInformation(
                    "Attempt {attempt}/{max} → POST {url} (timeout: {t}s)",
                    attempt, maxRetries, url, timeoutSeconds);

                var resp = await http.SendAsync(request, cts.Token);

                // ── Strip UTF-8 BOM if present ────────────────
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
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 10));
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
            catch (Exception ex)
            {
                // Non-retryable — surface immediately
                _logger.LogError(ex,
                    "Attempt {attempt}/{max} unexpected error for {url}",
                    attempt, maxRetries, url);
                throw;
            }
        }

        _logger.LogWarning(
            "All {max} attempts failed for {url}. Last error: {err}. Returning empty.",
            maxRetries, url, lastException?.Message);
        return new();
    }

    // ─────────────────────────────────────────────────────────
    // BOM STRIPPER
    // Some ERP responses arrive with a UTF-8 BOM (EF BB BF) that
    // causes JSON parsers to choke on the very first character.
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
    //
    // WHY THIS EXISTS:
    //   The ERP API emits "IndividualAH Battery1" with a mid-word
    //   space, which makes the key structurally ambiguous in some
    //   parsers. We collapse that specific space so the key becomes
    //   "IndividualAHBattery1", matching the [JsonProperty] in DjrValue.
    //
    // IMPORTANT — DO NOT add a general "collapse all spaced keys" regex.
    //   Every other spaced key ("Dealer Code", "Job No", "Service Head",
    //   "Total W/O Tax", etc.) is intentional and matched exactly by
    //   [JsonProperty("...")] in the DTOs. Collapsing them would make
    //   ALL those fields deserialize as null.
    // ─────────────────────────────────────────────────────────

    private static string SanitizeJson(string json)
    {
        // "IndividualAH Battery1" → "IndividualAHBattery1"  (and 2–6)
        json = System.Text.RegularExpressions.Regex.Replace(
            json,
            @"""IndividualAH Battery(\d+)""",
            m => $@"""IndividualAHBattery{m.Groups[1].Value}"""
        );

        return json;
    }

    // ─────────────────────────────────────────────────────────
    // TOLERANT JSON DESERIALIZER
    //
    // Fast path: standard JsonConvert — succeeds for well-formed responses.
    // Slow path: JObject item-by-item with error suppression — used when
    //   a single bad field (e.g. unescaped quote in a value) would
    //   otherwise abort the entire response.
    // ─────────────────────────────────────────────────────────

    private List<T> DeserializeTolerant<T>(
        string body, string url, int attempt, int maxRetries)
    {
        // Always sanitize first — fixes the IndividualAH Battery key
        body = SanitizeJson(body);

        // ── Fast path ────────────────────────────────────────
        try
        {
            var fastSettings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Ignore
            };

            var result = JsonConvert.DeserializeObject<ErpApiResponse<T>>(body, fastSettings);
            return result?.Valid == true ? result.Value ?? new() : new();
        }
        catch (JsonException firstEx)
        {
            _logger.LogWarning(
                "LOR fast parse failed for {url}: {msg}. Trying tolerant parse.",
                url, firstEx.Message);
        }

        // ── Slow path: item-by-item with error suppression ───
        try
        {
            var root = JObject.Parse(body);

            var valid = root["Valid"]?.Value<bool>() ?? false;
            if (!valid)
            {
                _logger.LogWarning(
                    "Tolerant parse: API returned Valid=false for {url}", url);
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
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tolerant parse also failed for {url}. Returning empty.", url);
            return new();
        }
    }

    // ─────────────────────────────────────────────────────────
    // REQUEST BUILDERS
    // Auth token is set per-request on the message, never on the
    // shared HttpClient, so concurrent calls cannot collide.
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