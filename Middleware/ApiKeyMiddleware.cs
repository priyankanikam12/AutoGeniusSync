// ─────────────────────────────────────────────────────────────
// FILE 1: Middleware/ApiKeyMiddleware.cs
// Simple API key authentication for external partners
// ─────────────────────────────────────────────────────────────

namespace AutoGeniusSync.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private const string ApiKeyHeader = "X-API-Key";

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for Swagger UI and health check
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/swagger") || path == "/health" || path == "/")
        {
            await _next(context);
            return;
        }

        // Check API key header
        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var key))
        {
            _logger.LogWarning("API request without key from {ip}", 
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new 
            { 
                error = "API key required. Add header: X-API-Key: <your-key>" 
            });
            return;
        }

        // Load all valid keys from config
        var validKeys = _config.GetSection("ApiKeys")
            .GetChildren()
            .Select(k => k.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .ToHashSet();

        if (!validKeys.Contains(key.ToString()))
        {
            _logger.LogWarning("Invalid API key used from {ip}", 
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new 
            { 
                error = "Invalid API key" 
            });
            return;
        }

        await _next(context);
    }
}