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
// Single entry point for the nightly full reload (daily timer + manual/Task-Scheduler endpoint share it,
// so two reloads can never run at once). Must be registered BEFORE the hosted service that uses it.
builder.Services.AddSingleton<NightlyReloadRunner>();
builder.Services.AddHostedService<SyncHostedService>();

// ─────────────────────────────────────────────────────────────
// API / SWAGGER
// ─────────────────────────────────────────────────────────────
// ReferenceLoopHandling.Ignore added as a safety net: DmsJobReportComplaint
// was missing [JsonIgnore] on its back-reference to DmsJobReport, which
// caused a "Self referencing loop detected" 500 on GET /api/jobreport (fixed
// separately in DmsJobReportComplaint.cs). This setting means any similar
// missed back-reference elsewhere in the app degrades to "loop member
// omitted" instead of a hard 500 — it does not replace fixing the model,
// just prevents this exact class of bug from taking an endpoint down again.
builder.Services.AddControllers().AddNewtonsoftJson(options =>
{
    options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
});
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