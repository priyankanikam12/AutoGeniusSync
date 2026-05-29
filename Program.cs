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
            sql.CommandTimeout(120);
            sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null   // uses EF Core's default transient list
            );
        }
    ));

// In Program.cs — set HttpClient timeout to "infinite", control it per-request instead
builder.Services.AddHttpClient<ErpApiService>(client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan; // ← per-request CTS controls this now
});

// ─────────────────────────────────────────────────────────────
// APPLICATION SERVICES
// ─────────────────────────────────────────────────────────────
//builder.Services.AddScoped<DataSyncService>();
builder.Services.AddSingleton<DataSyncService>();

// ─────────────────────────────────────────────────────────────
// BACKGROUND SERVICE
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
        app.Logger.LogError(ex, "Database connection failed. Check appsettings.json connection string.");
    }
}

// ─────────────────────────────────────────────────────────────
// MIDDLEWARE
// ─────────────────────────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "AutoGenius DMS v1"));

app.UseCors();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();