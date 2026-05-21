using AutoGeniusSync.BackgroundServices;
using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────
// WINDOWS SERVICE
// ─────────────────────────────────────────────
builder.Host.UseWindowsService();

builder.WebHost.UseUrls("http://0.0.0.0:7005");
// ─────────────────────────────────────────────────────────────
// DATABASE
// ─────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(120)
    ));

// ─────────────────────────────────────────────────────────────
// HTTP CLIENT (shared, no per-request base address so it's flexible)
// ─────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<ErpApiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ─────────────────────────────────────────────────────────────
// APPLICATION SERVICES
// ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<DataSyncService>();

// ErpApiService is AddHttpClient-registered above (scoped)
// but DataSyncService also needs it — register as scoped service too
//builder.Services.AddScoped<ErpApiService>();

// ─────────────────────────────────────────────────────────────
// BACKGROUND SERVICE (scheduled sync)
// ─────────────────────────────────────────────────────────────
builder.Services.AddHostedService<SyncHostedService>();

// ─────────────────────────────────────────────────────────────
// API / SWAGGER
// ─────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddNewtonsoftJson();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "AutoGenius DMS Sync API", Version = "v1" });
});

// ─────────────────────────────────────────────────────────────
// CORS (allow all for internal/dev use)
// ─────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─────────────────────────────────────────────────────────────
// ENSURE DB TABLES EXIST (runs SQL_Schema.sql via EF migrations)
// ─────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        // EnsureCreated creates tables from EF model if they don't exist
        await db.Database.EnsureCreatedAsync();
        app.Logger.LogInformation("Database connection verified.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database connection failed. Check appsettings.json connection string.");
    }
}

// ─────────────────────────────────────────────────────────────
// MIDDLEWARE
// ─────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AutoGenius DMS v1"));
}

app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();
