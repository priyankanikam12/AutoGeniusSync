using AutoGeniusSync.BackgroundServices;
using AutoGeniusSync.Data;
using AutoGeniusSync.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────
// PORT — Always use 7005, never fall back to default 5000
// This prevents "port in use" errors with Windows system process
// ─────────────────────────────────────────────────────────────
builder.WebHost.UseUrls("http://0.0.0.0:7005");

// ─────────────────────────────────────────────────────────────
// WINDOWS SERVICE
// ─────────────────────────────────────────────────────────────
builder.Host.UseWindowsService();

// ─────────────────────────────────────────────────────────────
// DATABASE
// ─────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(120)
    ));

// ─────────────────────────────────────────────────────────────
// HTTP CLIENT
// ─────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<ErpApiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
});

// ─────────────────────────────────────────────────────────────
// APPLICATION SERVICES
// ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<DataSyncService>();

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