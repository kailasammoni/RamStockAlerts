using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Engine;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;
using Serilog;
using Serilog.Events;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Universe;
using RamStockAlerts.Services.Universe;

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

// Bootstrap configuration early for mode + symbol resolution
var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
var initialConfig = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{env}.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

string ResolveMode(IConfiguration config)
{
    var modeValue = Environment.GetEnvironmentVariable("MODE")
                   ?? config["MODE"]
                   ?? config["Mode"]
                   ?? config["Ibkr:Mode"];
    return modeValue?.Trim() ?? string.Empty;
}


var mode = ResolveMode(initialConfig).ToLowerInvariant();

var dailyRollupRequested = initialConfig.GetValue("Report:DailyRollup", false);
if (dailyRollupRequested)
{
    var journalPath = initialConfig.GetValue<string>("Report:JournalPath");
    if (string.IsNullOrWhiteSpace(journalPath))
    {
        journalPath = initialConfig.GetValue<string>("ShadowTradeJournal:FilePath")
                      ?? Path.Combine("logs", "shadow-trade-journal.jsonl");
    }
    var writeToFile = initialConfig.GetValue("Report:WriteToFile", false);
    var outputPath = initialConfig.GetValue<string>("Report:OutputPath");

    Log.Information("Running daily rollup report for {JournalPath} (writeToFile={WriteToFile})",
        journalPath, writeToFile);

    // Phase 2.4: Build DI container for outcomes + rotation (or use no-arg constructor if disabled)
    var outcomesEnabled = initialConfig.GetValue("Outcomes:Enabled", true);
    var rotationEnabled = initialConfig.GetValue("JournalRotation:Enabled", true);

    if (outcomesEnabled || rotationEnabled)
    {
        // Build services for full integration
        var reporterServices = new ServiceCollection();
        
        // Register outcome services
        var reporterOutcomesFilePath = initialConfig.GetValue<string>("Outcomes:FilePath")
            ?? Path.Combine("logs", "trade-outcomes.jsonl");
        reporterServices.AddSingleton<ITradeOutcomeLabeler, TradeOutcomeLabeler>();
        reporterServices.AddSingleton<IOutcomeSummaryStore>(sp => new FileBasedOutcomeSummaryStore(reporterOutcomesFilePath));
        
        // Register rotation service
        reporterServices.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();
        
        var sp = reporterServices.BuildServiceProvider();
        var labeler = sp.GetRequiredService<ITradeOutcomeLabeler>();
        var store = sp.GetRequiredService<IOutcomeSummaryStore>();
        var rotationService = sp.GetRequiredService<IJournalRotationService>();

        var reporter = new DailyRollupReporter(labeler, store, rotationService);
        await reporter.RunAsync(journalPath, writeToFile, outputPath);
    }
    else
    {
        // Fallback: no outcomes or rotation
        var reporter = new DailyRollupReporter();
        await reporter.RunAsync(journalPath, writeToFile, outputPath);
    }
    return;
}

if (mode == "record")
{
    // RECORD MODE: Simple IBKR data recorder (writes L2 + tape to JSONL files)
    Log.Information("Starting in RECORD mode - IBKR data will be written to logs/");
    
    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<IbkrRecorderHostedService>();
        });
    
    var host = hostBuilder.Build();
    await host.RunAsync();
    return;
}

if (mode == "diagnostics")
{
    // DIAGNOSTICS MODE: Test subscription health for symbols on various exchanges
    Log.Information("Starting in DIAGNOSTICS mode - testing subscription health");
    
    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            // Add required services for diagnostics
            services.AddSingleton<IRequestIdSource>(sp => new IbkrRequestIdSource(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<ContractClassificationCache>();
            services.AddSingleton<ContractClassificationService>();
            services.AddHostedService<SubscriptionDiagnosticsHostedService>();
        });
    
    var host = hostBuilder.Build();
    await host.RunAsync();
    return;
}

if (mode == "replay")
{
    Log.Information("Starting in REPLAY mode - deterministic state reconstruction");

    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<IbkrReplayHostedService>();
        });

    var host = hostBuilder.Build();
    await host.RunAsync();
    return;
}

// NORMAL MODE: Full API application
var builder = WebApplication.CreateBuilder(args);

// Use Serilog
builder.Host.UseSerilog();

var tradingMode = builder.Configuration.GetValue<string>("TradingMode") ?? string.Empty;
var alertsEnabled = builder.Configuration.GetValue("AlertsEnabled", true);
var recordBlueprints = builder.Configuration.GetValue("RecordBlueprints", true);
var isShadowMode = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
var universeSource = builder.Configuration.GetValue<string>("Universe:Source");
var isLegacyUniverse = string.IsNullOrWhiteSpace(universeSource)
    || string.Equals(universeSource, "Legacy", StringComparison.OrdinalIgnoreCase);

if (isShadowMode)
{
    Log.Information("TradingMode=Shadow (AlertsEnabled={AlertsEnabled}, RecordBlueprints={RecordBlueprints})",
        alertsEnabled, recordBlueprints);
}

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
builder.Services.AddScoped<PerformanceTracker>();
builder.Services.AddSingleton<ApiQuotaTracker>();
builder.Services.AddScoped<BacktestService>();
builder.Services.AddSingleton<ShadowTradeJournal>();
builder.Services.AddSingleton<IShadowTradeJournal>(sp => sp.GetRequiredService<ShadowTradeJournal>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ShadowTradeJournal>());
builder.Services.AddHostedService<ShadowJournalHeartbeatService>();
builder.Services.AddSingleton<ScarcityController>();
builder.Services.AddSingleton<ShadowTradingCoordinator>();
builder.Services.AddSingleton<PreviewSignalEmitter>();
builder.Services.AddSingleton<IRequestIdSource>(sp => new IbkrRequestIdSource(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ContractClassificationCache>();
builder.Services.AddSingleton<ContractClassificationService>();
builder.Services.AddSingleton<DepthEligibilityCache>();
builder.Services.AddSingleton<DepthUniverseFilter>();
builder.Services.AddSingleton<StaticUniverseSource>();
builder.Services.AddSingleton<IbkrScannerUniverseSource>();
builder.Services.AddSingleton<UniverseService>();
builder.Services.AddSingleton<MarketDataSubscriptionManager>(sp =>
    new MarketDataSubscriptionManager(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<MarketDataSubscriptionManager>>(),
        sp.GetRequiredService<ContractClassificationService>(),
        sp.GetRequiredService<DepthEligibilityCache>(),
        sp.GetRequiredService<OrderFlowMetrics>(),
        sp.GetService<IShadowTradeJournal>()));

if (!isShadowMode && isLegacyUniverse)
{
    builder.Services.AddSingleton<LegacyUniverseBuilder>();
}

// Event store - use PostgreSQL-backed if enabled, otherwise use file-based store
if (usePostgreSQL)
{
    builder.Services.AddSingleton<IEventStore, PostgresEventStore>();
}
else
{
    builder.Services.AddSingleton<IEventStore, FileEventStore>();
}

if (!isShadowMode)
{
    builder.Services.AddSingleton<AlpacaStreamClient>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AlpacaStreamClient>());

    // Register Polygon background service only if Alpaca is not configured (fallback for daily aggregates)
    var alpacaKey = builder.Configuration["Alpaca:Key"];
    if (string.IsNullOrEmpty(alpacaKey))
    {
        builder.Services.AddHostedService<PolygonRestClient>();
        Log.Warning("Alpaca not configured. Using PolygonRestClient as fallback (development mode).");
    }
}
else
{
    Log.Information("Shadow mode active. AlpacaStreamClient hosting and PolygonRestClient are disabled.");
}

// Register order flow metrics and signal validation (IBKR Phase 3-4)
builder.Services.AddSingleton<OrderFlowMetrics>();
builder.Services.AddSingleton<OrderFlowSignalValidator>();

// Register outcome labeling and summary services (Phase 1)
var outcomesFilePath = builder.Configuration.GetValue<string>("Outcomes:FilePath")
    ?? Path.Combine("logs", "trade-outcomes.jsonl");
builder.Services.AddSingleton<ITradeOutcomeLabeler, TradeOutcomeLabeler>();
builder.Services.AddSingleton<IOutcomeSummaryStore>(sp => new FileBasedOutcomeSummaryStore(outcomesFilePath));
Log.Information("Outcome labeling services registered. Outcomes file: {Path}", outcomesFilePath);

// Register journal rotation service (Phase 2.3)
builder.Services.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();
Log.Information("Journal rotation service registered");

// Register Execution module (F0-F3)
var executionEnabled = builder.Configuration.GetValue("Execution:Enabled", false);
var executionBroker = builder.Configuration.GetValue("Execution:Broker", "Fake");

// Always register types (even if disabled) - controller will check enabled flag
// Bind ExecutionOptions from configuration
var executionOptions = new RamStockAlerts.Execution.Contracts.ExecutionOptions();
builder.Configuration.GetSection("Execution").Bind(executionOptions);

builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IExecutionLedger, 
    RamStockAlerts.Execution.Storage.InMemoryExecutionLedger>();
builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IRiskManager>(sp =>
{
    var maxNotional = builder.Configuration.GetValue("Execution:MaxNotionalUsd", 2000m);
    var maxShares = builder.Configuration.GetValue("Execution:MaxShares", 500m);
    return new RamStockAlerts.Execution.Risk.RiskManagerV0(executionOptions, maxNotional, maxShares);
});

// Broker selection based on config
if (string.Equals(executionBroker, "IBKR", StringComparison.OrdinalIgnoreCase))
{
    // TODO F4: Implement IbkrBrokerClient
    Log.Warning("Execution:Broker=IBKR requested but IbkrBrokerClient not yet implemented. Falling back to FakeBrokerClient.");
    builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IBrokerClient, 
        RamStockAlerts.Execution.Services.FakeBrokerClient>();
}
else
{
    builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IBrokerClient, 
        RamStockAlerts.Execution.Services.FakeBrokerClient>();
}

builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IExecutionService, 
    RamStockAlerts.Execution.Services.ExecutionService>();

// Add execution enabled flag to IConfiguration for controller to check
builder.Services.AddSingleton(sp => new { ExecutionEnabled = executionEnabled });

if (executionEnabled)
{
    Log.Information("Execution module ENABLED with {Broker} broker (maxNotional: {MaxNotional}, maxShares: {MaxShares})",
        executionBroker,
        builder.Configuration.GetValue("Execution:MaxNotionalUsd", 2000m),
        builder.Configuration.GetValue("Execution:MaxShares", 500m));
}
else
{
    Log.Information("Execution module DISABLED (endpoints will return 503). Set Execution:Enabled=true to enable.");
}

// Register IBKR market data client (Phase 1-2) if configured
var ibkrEnabled = builder.Configuration.GetValue("IBKR:Enabled", false);
if (ibkrEnabled)
{
    builder.Services.AddSingleton<IBkrMarketDataClient>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IBkrMarketDataClient>());
    Log.Information("IBkrMarketDataClient enabled");
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
