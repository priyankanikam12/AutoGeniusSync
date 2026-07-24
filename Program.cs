using AutoGeniusSync.BackgroundServices;
using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────
// PORT — Always use 7005, never fall back to default 5000
// This prevents "port in use" errors with Windows system process
// ─────────────────────────────────────────────────────────────
if (!builder.Environment.IsDevelopment())
{
    builder.Host.UseWindowsService();
    builder.WebHost.UseUrls("http://0.0.0.0:7005");
}
else
{
    builder.WebHost.UseUrls("http://localhost:7005");
}

// ─────────────────────────────────────────────────────────────
// DATABASE
// ─────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql =>
        {
            sql.CommandTimeout(300);
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(15),
                errorNumbersToAdd: null);
        }
    ), ServiceLifetime.Scoped);

// ─────────────────────────────────────────────────────────────
// HTTP CLIENT (DEFAULT API CLIENTS)
// ─────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();

// ─────────────────────────────────────────────────────────────
// ERP API FIX (IMPORTANT - prevents HttpClient disposed issue)
// ─────────────────────────────────────────────────────────────
var erpTimeout = builder.Configuration.GetValue<int>(
    "AutoGeniusERP:HttpTimeoutSeconds",
    120
);

builder.Services.AddHttpClient("ErpApi", client =>
{
    client.Timeout = TimeSpan.FromSeconds(erpTimeout + 10);
});

// ─────────────────────────────────────────────────────────────
// APPLICATION SERVICES
// IMPORTANT: DataSyncService must be Scoped (NOT Singleton)
// because it uses IServiceScopeFactory to create DB contexts.
// Using Singleton caused multiple concurrent scopes hitting the
// DB connection pool simultaneously → connection timeout.
// ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<DataSyncService>();
builder.Services.AddSingleton<ErpApiService>();

// ─────────────────────────────────────────────────────────────
// BACKGROUND SERVICE
// ─────────────────────────────────────────────────────────────
builder.Services.AddHostedService<SyncHostedService>();

// ─────────────────────────────────────────────────────────────
// API / SWAGGER
// ─────────────────────────────────────────────────────────────
builder.Services.AddControllers().AddNewtonsoftJson();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "AutoGenius DMS Sync API", Version = "v1" }));

// ─────────────────────────────────────────────────────────────
// CORS
// ─────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// ENSURE DB TABLES EXIST
// ─────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
        app.Logger.LogInformation("Database connection verified.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database connection failed.");
    }
}

// ─────────────────────────────────────────────────────────────
// MIDDLEWARE
// ─────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AutoGenius DMS v1"));

app.UseCors();
app.UseMiddleware<AutoGeniusSync.Middleware.ApiKeyMiddleware>();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();