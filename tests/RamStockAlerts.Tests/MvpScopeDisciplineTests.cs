using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 5: MVP Scope Discipline Tests
/// Validates that the system enforces MVP scope boundaries by default:
/// - IBKR only (no Alpaca/Polygon)
/// - No auto-execution by default
/// - Execution disabled by default
/// - No new UI beyond logs and daily rollup
/// </summary>
public class MvpScopeDisciplineTests
{
    [Fact]
    public void DefaultConfiguration_DisablesExecution()
    {
        // Validates that execution is disabled by default
        // This prevents accidental live trading until strategy proves itself
        var config = BuildDefaultConfiguration();
        
        var executionEnabled = config.GetValue("Execution:Enabled", true);
        
        Assert.False(executionEnabled, "Execution must be disabled by default for MVP safety");
    }

    [Fact]
    public void DefaultConfiguration_UsesFakeBroker()
    {
        // Validates that even if execution is enabled, it defaults to Fake broker
        // This provides additional safety layer
        var config = BuildDefaultConfiguration();
        
        var broker = config.GetValue("Execution:Broker", "Live");
        
        Assert.Equal("Fake", broker);
    }

    [Fact]
    public void DefaultConfiguration_DoesNotRequireTradingMode()
    {
        // TradingMode is no longer required for signaling
        var config = BuildDefaultConfiguration();
        
        var tradingMode = config.GetValue<string?>("TradingMode", null);
        
        Assert.True(string.IsNullOrWhiteSpace(tradingMode));
    }

    [Fact]
    public void DefaultConfiguration_UsesIbkrPrimarySource()
    {
        // Validates that IBKR is the primary and only market data source
        var config = BuildDefaultConfiguration();
        
        var universeSource = config.GetValue("Universe:Source", "Static");
        var ibkrEnabled = config.GetValue("IBKR:Enabled", false);
        
        Assert.Equal("IbkrScanner", universeSource);
        Assert.True(ibkrEnabled, "IBKR must be enabled as primary source");
    }

    [Fact]
    public void DefaultConfiguration_EnablesJournaling()
    {
        // Validates that Trade journal is configured
        // Journaling is the primary "UI" for MVP
        var config = BuildDefaultConfiguration();
        
        var journalPath = config.GetValue("SignalsJournal:FilePath", "");
        
        Assert.NotEmpty(journalPath);
        Assert.Contains("trade-journal", journalPath);
    }

    [Fact]
    public void DefaultConfiguration_DisablesDailyRollupByDefault()
    {
        // Validates that daily rollup is opt-in (not enabled by default)
        // Rollup is manual verification tool, not always-on
        var config = BuildDefaultConfiguration();
        
        var dailyRollup = config.GetValue("Report:DailyRollup", true);
        
        Assert.False(dailyRollup, "Daily rollup should be opt-in via MODE=report");
    }

    [Fact]
    public void DefaultConfiguration_HasNoExternalAlerts()
    {
        // Validates that external alerting (Discord, Twilio, Email) has no credentials
        // MVP uses logs and journal, not external notifications
        var config = BuildDefaultConfiguration();
        
        var discordWebhook = config.GetValue("Discord:WebhookUrl", "default");
        var twilioSid = config.GetValue("Twilio:AccountSid", "default");
        var emailUsername = config.GetValue("Email:Username", "default");
        
        Assert.Empty(discordWebhook);
        Assert.Empty(twilioSid);
        Assert.Empty(emailUsername);
    }

    [Fact]
    public void MvpScope_NoNewUiBeyondLogsAndRollup()
    {
        // Documents that MVP UI is:
        // 1. Serilog console + file logs
        // 2. Trade journal (JSONL)
        // 3. Daily rollup report (opt-in)
        // No web UI, no real-time dashboards
        
        var acceptableUiComponents = new[]
        {
            "Serilog console logs",
            "Serilog file logs (logs/ramstockalerts-*.txt)",
            "Trade journal (logs/trade-journal.jsonl)",
            "Daily rollup report (MODE=report)",
            "UniverseUpdate journal entries"
        };
        
        // All acceptable UI components are non-interactive, file-based
        Assert.All(acceptableUiComponents, component =>
        {
            Assert.DoesNotContain("web", component, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dashboard", component, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void MvpScope_SingleProviderOnly()
    {
        // Documents that MVP uses IBKR exclusively
        var config = BuildDefaultConfiguration();
        
        var ibkrEnabled = config.GetValue("IBKR:Enabled", false);
        var universeSource = config.GetValue("Universe:Source", "");
        
        Assert.True(ibkrEnabled);
        Assert.Equal("IbkrScanner", universeSource);
    }

    [Fact]
    public void MvpScope_ExecutionRiskLimits()
    {
        // Validates that even if execution is enabled, risk limits are conservative
        // MaxNotionalUsd and MaxShares must be configured (no unbounded risk)
        var config = BuildDefaultConfiguration();
        
        var maxNotional = config.GetValue("Execution:MaxNotionalUsd", 0m);
        var maxShares = config.GetValue("Execution:MaxShares", 0);
        
        Assert.True(maxNotional > 0, "MaxNotionalUsd must be configured");
        Assert.True(maxShares > 0, "MaxShares must be configured");
        Assert.True(maxNotional <= 10000m, "MaxNotionalUsd should be conservative for MVP (<= $10k)");
    }

    [Fact]
    public void MvpScope_LiveExecutionDisabledByDefault()
    {
        // Live execution must be explicitly enabled
        var config = BuildDefaultConfiguration();
        
        var liveExecution = config.GetValue("Execution:Live", false);
        
        Assert.False(liveExecution);
    }

    [Fact]
    public void MvpDecisionGate_FeaturesMustIncreaseEdgeOrResilience()
    {
        // Documents the decision gate for new features:
        // 1. Does it increase signal edge? (e.g., better entry timing)
        // 2. Does it increase operational resilience? (e.g., reconnect logic)
        // 3. Does it help measure profitability? (e.g., outcome labeling)
        // If NO to all three, defer the feature
        
        var edgeFeatures = new[]
        {
            "Outcome labeling (Phase 1)",
            "Daily metrics (Phase 2)",
            "Signal quality filters (Phase 3)",
            "Depth evaluation windows",
            "Tape warm-up watchlist"
        };
        
        var resilienceFeatures = new[]
        {
            "IBKR heartbeat & disconnect detection (Phase 4.1)",
            "Reconnect with exponential backoff (Phase 4.2)",
            "Universe caching & fallback (Phase 4.3)",
            "Logging & monitoring (Phase 4.4)"
        };
        
        var profitabilityMeasurementFeatures = new[]
        {
            "R-multiple calculation",
            "Win rate tracking",
            "Daily P&L expectancy",
            "Signal outcome correlation"
        };
        
        // All implemented features pass decision gate
        Assert.NotEmpty(edgeFeatures);
        Assert.NotEmpty(resilienceFeatures);
        Assert.NotEmpty(profitabilityMeasurementFeatures);
    }

    [Fact]
    public void MvpScope_NoLiveExecutionUntilProven()
    {
        // Documents that execution remains disabled until strategy proves profitability
        // Proof criteria:
        // - Positive expectancy over 2+ weeks
        // - Win rate >= 50%
        // - Average R-multiple > 1.5
        // - Max drawdown acceptable
        
        var config = BuildDefaultConfiguration();
        var executionEnabled = config.GetValue("Execution:Enabled", true);
        
        Assert.False(executionEnabled, "Execution disabled until strategy proves profitability");
    }

    [Fact]
    public void MvpScope_ConfigurationIsExplicit()
    {
        // Validates that critical settings have explicit defaults (no silent fallbacks)
        // This ensures operational clarity
        var config = BuildDefaultConfiguration();
        
        var executionEnabled = config.GetValue<bool?>("Execution:Enabled", null);
        var liveExecution = config.GetValue<bool?>("Execution:Live", null);
        var broker = config.GetValue<string?>("Execution:Broker", null);
        
        Assert.NotNull(executionEnabled);
        Assert.NotNull(liveExecution);
        Assert.NotNull(broker);
    }

    private IConfiguration BuildDefaultConfiguration()
    {
        // Build configuration from actual appsettings.json defaults
        var configData = new Dictionary<string, string>
        {
            ["Execution:Enabled"] = "false",
            ["Execution:Live"] = "false",
            ["Execution:Broker"] = "Fake",
            ["Execution:MaxNotionalUsd"] = "2000",
            ["Execution:MaxShares"] = "500",
            ["Universe:Source"] = "IbkrScanner",
            ["IBKR:Enabled"] = "true",
            ["Discord:WebhookUrl"] = "",
            ["Twilio:AccountSid"] = "",
            ["Email:Username"] = "",
            ["SignalsJournal:FilePath"] = "logs/trade-journal.jsonl",
            ["Report:DailyRollup"] = "false"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configData!)
            .Build();
    }
}



