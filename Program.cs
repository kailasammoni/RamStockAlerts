using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Engine;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;
using Serilog;
using Serilog.Events;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/ramstockalerts-.txt", 
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Use Serilog
builder.Host.UseSerilog();

// Add Application Insights
var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrEmpty(appInsightsConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = appInsightsConnectionString;
    });
    
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .WriteTo.ApplicationInsights(appInsightsConnectionString, TelemetryConverter.Traces)
        .CreateLogger();
}

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "RamStockAlerts API", Version = "v1" });
});

// Database configuration - support both SQLite and PostgreSQL
var usePostgreSQL = builder.Configuration.GetValue<bool>("UsePostgreSQL");
if (usePostgreSQL)
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));
    Log.Information("Using PostgreSQL database");
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") 
            ?? "Data Source=ramstockalerts.db"));
    Log.Information("Using SQLite database");
}

// Caching for universe and circuit breaker
builder.Services.AddMemoryCache();

// Add HttpClient for alert channels and Polygon
builder.Services.AddHttpClient<DiscordAlertChannel>();
builder.Services.AddHttpClient("Polygon");
builder.Services.AddHttpClient();

// Register alert channels in order (Discord -> SMS -> Email)
// They will be resolved in the order they are registered for failover
builder.Services.AddScoped<IAlertChannel, DiscordAlertChannel>();
builder.Services.AddScoped<IAlertChannel, SmsAlertChannel>();
builder.Services.AddScoped<IAlertChannel, EmailAlertChannel>();
builder.Services.AddScoped<MultiChannelNotificationService>();

// Register services
builder.Services.AddScoped<SignalService>();
builder.Services.AddScoped<AlertThrottlingService>();
builder.Services.AddSingleton<AlpacaTradingClient>();
// Keep DiscordNotificationService for backward compatibility (legacy code may use it)
builder.Services.AddHttpClient<DiscordNotificationService>();
builder.Services.AddScoped<DiscordNotificationService>();
builder.Services.AddSingleton<SignalValidator>();
builder.Services.AddSingleton<TradeBlueprint>();
builder.Services.AddSingleton<CircuitBreakerService>();
builder.Services.AddSingleton<UniverseBuilder>();
builder.Services.AddScoped<PerformanceTracker>();
builder.Services.AddSingleton<ApiQuotaTracker>();
builder.Services.AddScoped<BacktestService>();

// Event store - use PostgreSQL-backed if enabled, otherwise use file-based store
if (usePostgreSQL)
{
    builder.Services.AddSingleton<IEventStore, PostgresEventStore>();
}
else
{
    builder.Services.AddSingleton<IEventStore, FileEventStore>();
}

// Register Alpaca real-time streaming service
builder.Services.AddSingleton<AlpacaStreamClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlpacaStreamClient>());

// Register Polygon background service only if Alpaca is not configured (fallback for daily aggregates)
var alpacaKey = builder.Configuration["Alpaca:Key"];
if (string.IsNullOrEmpty(alpacaKey))
{
    builder.Services.AddHostedService<PolygonRestClient>();
    Log.Warning("Alpaca not configured. Using PolygonRestClient as fallback (development mode).");
}

// Health checks
var healthChecks = builder.Services.AddHealthChecks();

if (usePostgreSQL)
{
    healthChecks.AddNpgSql(
        builder.Configuration.GetConnectionString("PostgreSQL") ?? "",
        name: "postgresql",
        tags: new[] { "db", "sql", "postgresql" });
}
else
{
    healthChecks.AddDbContextCheck<AppDbContext>(
        name: "sqlite",
        tags: new[] { "db", "sql", "sqlite" });
}

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Ensure database is created (skip in test environment)
if (!app.Environment.IsEnvironment("Testing"))
{
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (usePostgreSQL)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            db.Database.EnsureCreated();
        }
    }
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => 
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RamStockAlerts API v1");
        c.RoutePrefix = string.Empty; // Swagger at root URL
    });
    app.UseCors("DevCors");
}

// Only use HTTPS redirection in production or when HTTPS is available
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();

// Health check endpoints
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false  // Just check if app is running
});

// Graceful shutdown handlers
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Application is stopping, initiating graceful shutdown...");
});

lifetime.ApplicationStopped.Register(() =>
{
    Log.Information("Application stopped.");
    Log.CloseAndFlush();
});

try
{
    Log.Information("Starting RamStockAlerts API");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Make the implicit Program class public for testing
public partial class Program { }
