using System;
using RamStockAlerts.Engine;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Signals;
using RamStockAlerts.Services.Reporting;
using Serilog;
using Serilog.Events;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Universe;
using RamStockAlerts.Services.Universe;
using RamStockAlerts.Execution.Reporting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // Keep startup "Now listening on ..." visible even when the rest of Microsoft logs are suppressed.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
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

void LogSessionStart(string label) =>
    Log.Information("[Session] Starting {Label} session at {Time:O}", label, DateTimeOffset.UtcNow);

void LogSessionEnd(string label) =>
    Log.Information("[Session] Ending {Label} session at {Time:O}", label, DateTimeOffset.UtcNow);

string ResolveJournalPathForReport(IConfiguration config)
{
    var configured = config.GetValue<string>("Report:JournalPath");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    var path = config.GetValue<string>("SignalsJournal:FilePath");
    if (!string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    path = config.GetValue<string>("TradeJournal:FilePath");
    if (!string.IsNullOrWhiteSpace(path))
    {
        Log.Warning("Deprecated config key TradeJournal:FilePath used; migrate to SignalsJournal:FilePath.");
        return path;
    }

    path = config.GetValue<string>("ShadowTradeJournal:FilePath");
    if (!string.IsNullOrWhiteSpace(path))
    {
        Log.Warning("Deprecated config key ShadowTradeJournal:FilePath used; migrate to SignalsJournal:FilePath.");
        return path;
    }

    var defaultPath = Path.Combine("logs", "trade-journal.jsonl");
    var legacyPath = Path.Combine("logs", "shadow-trade-journal.jsonl");
    if (!File.Exists(defaultPath) && File.Exists(legacyPath))
    {
        Log.Warning("Legacy journal file detected at {Path}. Migrate to trade-journal.jsonl.", legacyPath);
        return legacyPath;
    }

    return defaultPath;
}

var dailyRollupRequested = initialConfig.GetValue("Report:DailyRollup", false);
if (dailyRollupRequested)
{
    var journalPath = ResolveJournalPathForReport(initialConfig);
    var writeToFile = initialConfig.GetValue("Report:WriteToFile", false);
    var outputPath = initialConfig.GetValue<string>("Report:OutputPath");

    Log.Information("Running daily rollup report for {JournalPath} (writeToFile={WriteToFile})",
        journalPath, writeToFile);

    var outcomesEnabled = initialConfig.GetValue("Outcomes:Enabled", true);
    var rotationEnabled = initialConfig.GetValue("JournalRotation:Enabled", true);

    if (outcomesEnabled || rotationEnabled)
    {
        var reporterServices = new ServiceCollection();
        
        var reporterOutcomesFilePath = initialConfig.GetValue<string>("Outcomes:FilePath")
            ?? Path.Combine("logs", "trade-outcomes.jsonl");
        reporterServices.AddSingleton<ITradeOutcomeLabeler, TradeOutcomeLabeler>();
        reporterServices.AddSingleton<IOutcomeSummaryStore>(sp => new FileBasedOutcomeSummaryStore(reporterOutcomesFilePath));
        reporterServices.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();
        
        using var sp = reporterServices.BuildServiceProvider();
        var labeler = sp.GetRequiredService<ITradeOutcomeLabeler>();
        var store = sp.GetRequiredService<IOutcomeSummaryStore>();
        var rotationService = sp.GetRequiredService<IJournalRotationService>();

        var reporter = new DailyRollupReporter(labeler, store, rotationService);
        await reporter.RunAsync(journalPath, writeToFile, outputPath);
    }
    else
    {
        var reporter = new DailyRollupReporter();
        await reporter.RunAsync(journalPath, writeToFile, outputPath);
    }
    return;
}

var executionRollupRequested = initialConfig.GetValue("Report:ExecutionDailyRollup", false);
if (executionRollupRequested)
{
    var ledgerPath = initialConfig.GetValue<string>("Report:ExecutionLedgerPath");
    if (string.IsNullOrWhiteSpace(ledgerPath))
    {
        ledgerPath = initialConfig.GetValue<string>("Execution:Ledger:FilePath")
                     ?? Path.Combine("logs", "execution-ledger.jsonl");
    }

    var writeToFile = initialConfig.GetValue("Report:WriteToFile", false);
    var outputPath = initialConfig.GetValue<string>("Report:OutputPath");

    Log.Information("Running execution EOD report for {LedgerPath} (writeToFile={WriteToFile})",
        ledgerPath, writeToFile);

    var reporter = new ExecutionDailyReporter();
    await reporter.RunAsync(ledgerPath, writeToFile, outputPath);
    return;
}

if (mode == "monitor")
{
    Log.Information("Starting in MONITOR mode - tailing market-hours log streams");
    LogSessionStart("Monitor mode");

    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<MarketHoursLogIngestor>();
        });

    var host = hostBuilder.Build();
    await host.RunAsync();
    LogSessionEnd("Monitor mode");
    return;
}

if (mode == "record")
{
    Log.Information("Starting in RECORD mode - IBKR data will be written to logs/");
    LogSessionStart("Record mode");
    
    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<IbkrRecorderHostedService>();
        });
    
    var host = hostBuilder.Build();
    await host.RunAsync();
    LogSessionEnd("Record mode");
    return;
}

if (mode == "diagnostics")
{
    Log.Information("Starting in DIAGNOSTICS mode - testing subscription health");
    LogSessionStart("Diagnostics mode");
    
    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<IRequestIdSource>(sp => new IbkrRequestIdSource(sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<ContractClassificationCache>();
            services.AddSingleton<ContractClassificationService>();
            services.AddHostedService<SubscriptionDiagnosticsHostedService>();
        });
    
    var host = hostBuilder.Build();
    await host.RunAsync();
    LogSessionEnd("Diagnostics mode");
    return;
}

if (mode == "replay")
{
    Log.Information("Starting in REPLAY mode - deterministic state reconstruction");
    LogSessionStart("Replay mode");

    var hostBuilder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.AddHostedService<IbkrReplayHostedService>();
        });

    var host = hostBuilder.Build();
    await host.RunAsync();
    LogSessionEnd("Replay mode");
    return;
}

// NORMAL MODE: Full API application
var builder = WebApplication.CreateBuilder(args);

// Use Serilog
builder.Host.UseSerilog();

// Enable concurrent hosted service startup to prevent one service from blocking others
builder.Services.Configure<HostOptions>(options =>
{
    options.ServicesStartConcurrently = true;
    options.ServicesStopConcurrently = true;
});

var recordBlueprints = builder.Configuration.GetValue("RecordBlueprints", true);
const string sessionLabel = "Trading";
Log.Information("Signal pipeline enabled (RecordBlueprints={RecordBlueprints})", recordBlueprints);
LogSessionStart(sessionLabel);

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

// Caching for universe
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Discord notification services
builder.Services.AddSingleton<DiscordNotificationSettingsStore>();
builder.Services.AddSingleton<DiscordDeliveryStatusStore>();
builder.Services.AddSingleton<DiscordNotificationService>();

// Register services
builder.Services.AddSingleton<TradeJournal>();
builder.Services.AddSingleton<ITradeJournal>(sp => sp.GetRequiredService<TradeJournal>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TradeJournal>());
builder.Services.AddHostedService<TradeJournalHeartbeatService>();

if (builder.Configuration.GetValue("Monitoring:Enabled", false))
{
    builder.Services.AddHostedService<MarketHoursLogIngestor>();
}
builder.Services.AddSingleton<ScarcityController>();
builder.Services.AddSingleton<SignalCoordinator>();
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
        sp.GetService<ITradeJournal>()));

// Register order flow metrics and signal validation
builder.Services.AddSingleton<OrderFlowMetrics>();
builder.Services.AddSingleton<OrderFlowSignalValidator>();
builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IPostSignalMonitor>(sp =>
    sp.GetRequiredService<SignalCoordinator>());

if (OperatingSystem.IsWindows())
{
    builder.Services.AddHostedService<SystemSleepPreventer>();
    Log.Information("[System] Windows keep-awake helper registered.");
}

// Register outcome labeling and summary services
var outcomesFilePath = builder.Configuration.GetValue<string>("Outcomes:FilePath")
    ?? Path.Combine("logs", "trade-outcomes.jsonl");
builder.Services.AddSingleton<ITradeOutcomeLabeler, TradeOutcomeLabeler>();
builder.Services.AddSingleton<IOutcomeSummaryStore>(sp => new FileBasedOutcomeSummaryStore(outcomesFilePath));
Log.Information("Outcome labeling services registered. Outcomes file: {Path}", outcomesFilePath);

// Register journal rotation service
builder.Services.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();
Log.Information("Journal rotation service registered");

// Register Execution module
var executionEnabled = builder.Configuration.GetValue("Execution:Enabled", false);
var executionBroker = builder.Configuration.GetValue("Execution:Broker", "Fake");

var executionOptions = new RamStockAlerts.Execution.Contracts.ExecutionOptions();
builder.Configuration.GetSection("Execution").Bind(executionOptions);
builder.Services.AddSingleton(executionOptions);

var executionLedgerType = builder.Configuration.GetValue<string>("Execution:Ledger:Type") ?? "InMemory";
if (string.Equals(executionLedgerType, "Jsonl", StringComparison.OrdinalIgnoreCase))
{
    var ledgerPath = builder.Configuration.GetValue<string>("Execution:Ledger:FilePath")
                    ?? Path.Combine("logs", "execution-ledger.jsonl");
    builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IExecutionLedger>(
        _ => new RamStockAlerts.Execution.Storage.JsonlExecutionLedger(ledgerPath));
    Log.Information("Execution ledger: Jsonl ({Path})", ledgerPath);
}
else
{
    builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IExecutionLedger,
        RamStockAlerts.Execution.Storage.InMemoryExecutionLedger>();
    Log.Information("Execution ledger: InMemory");
}
builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IOrderStateTracker>(sp =>
    new RamStockAlerts.Execution.Services.OrderStateTracker(
        sp.GetRequiredService<ILogger<RamStockAlerts.Execution.Services.OrderStateTracker>>(),
        sp.GetService<RamStockAlerts.Execution.Interfaces.IExecutionLedger>(),
        sp.GetService<RamStockAlerts.Execution.Interfaces.IPostSignalMonitor>()));
builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IRiskManager>(sp =>
{
    var maxNotional = builder.Configuration.GetValue("Execution:MaxNotionalUsd", 2000m);
    var maxShares = builder.Configuration.GetValue("Execution:MaxShares", 500m);
    return new RamStockAlerts.Execution.Risk.RiskManagerV0(
        executionOptions,
        sp.GetService<RamStockAlerts.Execution.Interfaces.IOrderStateTracker>(),
        maxNotional,
        maxShares);
});

// Broker selection
if (string.Equals(executionBroker, "IBKR", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<RamStockAlerts.Execution.Interfaces.IBrokerClient, 
        RamStockAlerts.Execution.Services.IbkrBrokerClient>();
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

// Register IBKR market data client
var ibkrEnabled = builder.Configuration.GetValue("IBKR:Enabled", false);
if (ibkrEnabled)
{
    builder.Services.AddSingleton<IBkrMarketDataClient>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IBkrMarketDataClient>());
    Log.Information("IBkrMarketDataClient enabled");
}

// Health checks
builder.Services.AddHealthChecks();

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
var shutdownStartedUtc = DateTimeOffset.MinValue;
var processId = Environment.ProcessId;
var forceExitAfterSeconds = builder.Configuration.GetValue<int?>("Shutdown:ForceExitAfterSeconds")
    ?? (app.Environment.IsDevelopment() ? 30 : (int?)null);

lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var urls = (addresses != null && addresses.Count > 0) ? string.Join(", ", addresses) : "(unknown)";
        Log.Information("Application started. Pid={Pid} Listening on {Urls}", processId, urls);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Application started (unable to resolve listening URLs).");
    }
});

lifetime.ApplicationStopping.Register(() =>
{
    shutdownStartedUtc = DateTimeOffset.UtcNow;
    Log.Information("Application is stopping (Pid={Pid}), initiating graceful shutdown...", processId);
    LogSessionEnd(sessionLabel);

    if (forceExitAfterSeconds is > 0)
    {
        Log.Warning(
            "[Shutdown] Force-exit watchdog armed: {Seconds}s. Set Shutdown:ForceExitAfterSeconds=0 to disable.",
            forceExitAfterSeconds.Value);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(forceExitAfterSeconds.Value));
                Console.Error.WriteLine(
                    $"[Shutdown] Force-exit watchdog firing after {forceExitAfterSeconds.Value}s (host still stopping).");
            }
            catch
            {
                return;
            }

            try
            {
                Log.CloseAndFlush();
            }
            catch
            {
                // Best-effort only.
            }

            Environment.Exit(0);
        });
    }
});

lifetime.ApplicationStopped.Register(() =>
{
    var durationMs = shutdownStartedUtc == DateTimeOffset.MinValue
        ? (double?)null
        : (DateTimeOffset.UtcNow - shutdownStartedUtc).TotalMilliseconds;

    if (durationMs.HasValue)
    {
        Log.Information(
            "Application stopped. Pid={Pid} ShutdownDurationMs={ShutdownDurationMs}",
            processId,
            (long)durationMs.Value);
    }
    else
    {
        Log.Information("Application stopped. Pid={Pid}", processId);
    }
});

try
{
    Log.Information("Starting RamStockAlerts API (Pid={Pid})", processId);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    try
    {
        _ = Task.Run(() => Log.CloseAndFlush());
    }
    catch
    {
        // Best-effort only.
    }
}

// Make the implicit Program class public for testing
public partial class Program { }
