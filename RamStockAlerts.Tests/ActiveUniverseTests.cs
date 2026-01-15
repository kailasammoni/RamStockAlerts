using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Unit tests for ActiveUniverse feature in MarketDataSubscriptionManager.
/// 
/// ActiveUniverse Definition (strict contract):
/// A symbol is Active IFF all are true:
/// 1) Tape subscription is enabled (MarketDataSubscriptionManager.IsTapeEnabled(symbol) == true)
/// 2) Depth subscription is enabled/active for symbol
/// 3) Tick-by-tick subscription is enabled/active for symbol
/// 4) Tape activity gate is satisfied (ShadowTradingHelpers.GetTapeStatus(...).Kind == Ready)
///    Note: Tape status check is done at evaluation time by strategy, not by MarketDataSubscriptionManager
/// 
/// Constraints:
/// - Depth cap: max 3 active symbols with depth at any time
/// - Tick-by-tick cap: max 6 active symbols with tick-by-tick
/// - Tape may be subscribed for all candidates, but only Active symbols are evaluated
/// </summary>
public class ActiveUniverseTests
{
    [Fact]
    public void IsActiveSymbol_WithNoSubscriptions_ReturnsFalse()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        var result = manager.IsActiveSymbol("AAPL");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsActiveSymbol_WithNullOrEmpty_ReturnsFalse()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act & Assert
        Assert.False(manager.IsActiveSymbol(null!));
        Assert.False(manager.IsActiveSymbol(string.Empty));
        Assert.False(manager.IsActiveSymbol("   "));
    }

    [Fact]
    public void GetActiveUniverseSnapshot_InitiallyEmpty_ReturnsEmpty()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        var snapshot = manager.GetActiveUniverseSnapshot();

        // Assert
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void SetActiveUniverse_WithNullSymbols_ThrowsArgumentNullException()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => manager.SetActiveUniverse(null!, "test"));
    }

    [Fact]
    public void SetActiveUniverse_WithNullReason_ThrowsArgumentException()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => manager.SetActiveUniverse(new[] { "AAPL" }, null!));
        Assert.Throws<ArgumentException>(() => manager.SetActiveUniverse(new[] { "AAPL" }, string.Empty));
        Assert.Throws<ArgumentException>(() => manager.SetActiveUniverse(new[] { "AAPL" }, "   "));
    }

    [Fact]
    public void SetActiveUniverse_WithValidSymbols_UpdatesSnapshot()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        manager.SetActiveUniverse(new[] { "AAPL", "MSFT", "GOOGL" }, "TestReason");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Contains("AAPL", snapshot);
        Assert.Contains("GOOGL", snapshot);
        Assert.Contains("MSFT", snapshot);
    }

    [Fact]
    public void SetActiveUniverse_NormalizesSymbols_ToUpperCase()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        manager.SetActiveUniverse(new[] { "aapl", "Msft", "GOOGL" }, "TestReason");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        Assert.Contains("AAPL", snapshot);
        Assert.Contains("MSFT", snapshot);
        Assert.Contains("GOOGL", snapshot);
    }

    [Fact]
    public void SetActiveUniverse_IgnoresWhitespace_AndNullEntries()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        manager.SetActiveUniverse(new[] { "AAPL", "  ", null!, "MSFT", "" }, "TestReason");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains("AAPL", snapshot);
        Assert.Contains("MSFT", snapshot);
    }

    [Fact]
    public void SetActiveUniverse_OrdersSymbols_Alphabetically()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);

        // Act
        manager.SetActiveUniverse(new[] { "MSFT", "AAPL", "GOOGL" }, "TestReason");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        var list = snapshot.ToList();
        Assert.Equal("AAPL", list[0]);
        Assert.Equal("GOOGL", list[1]);
        Assert.Equal("MSFT", list[2]);
    }

    [Fact]
    public void IsActiveSymbol_AfterSetActiveUniverse_ReturnsTrue()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);
        manager.SetActiveUniverse(new[] { "AAPL", "MSFT" }, "TestReason");

        // Act & Assert
        Assert.True(manager.IsActiveSymbol("AAPL"));
        Assert.True(manager.IsActiveSymbol("MSFT"));
        Assert.True(manager.IsActiveSymbol("aapl")); // Case insensitive
        Assert.False(manager.IsActiveSymbol("GOOGL"));
    }

    [Fact]
    public void SetActiveUniverse_Twice_ReplacesOldWithNew()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);
        manager.SetActiveUniverse(new[] { "AAPL", "MSFT" }, "Initial");

        // Act
        manager.SetActiveUniverse(new[] { "GOOGL", "TSLA" }, "Updated");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Contains("GOOGL", snapshot);
        Assert.Contains("TSLA", snapshot);
        Assert.DoesNotContain("AAPL", snapshot);
        Assert.DoesNotContain("MSFT", snapshot);

        Assert.False(manager.IsActiveSymbol("AAPL"));
        Assert.False(manager.IsActiveSymbol("MSFT"));
        Assert.True(manager.IsActiveSymbol("GOOGL"));
        Assert.True(manager.IsActiveSymbol("TSLA"));
    }

    [Fact]
    public void SetActiveUniverse_ImmutableSnapshot_DoesNotAffectOriginal()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);
        manager.SetActiveUniverse(new[] { "AAPL", "MSFT" }, "TestReason");

        // Act
        var snapshot1 = manager.GetActiveUniverseSnapshot();
        manager.SetActiveUniverse(new[] { "GOOGL" }, "Updated");
        var snapshot2 = manager.GetActiveUniverseSnapshot();

        // Assert
        Assert.Equal(2, snapshot1.Count);
        Assert.Contains("AAPL", snapshot1);
        Assert.Contains("MSFT", snapshot1);

        Assert.Single(snapshot2);
        Assert.Contains("GOOGL", snapshot2);
    }

    [Fact]
    public void SetActiveUniverse_EmptyList_ClearsActiveUniverse()
    {
        // Arrange
        var config = CreateConfig();
        var manager = CreateManager(config);
        manager.SetActiveUniverse(new[] { "AAPL", "MSFT" }, "Initial");

        // Act
        manager.SetActiveUniverse(Array.Empty<string>(), "Cleared");

        // Assert
        var snapshot = manager.GetActiveUniverseSnapshot();
        Assert.Empty(snapshot);
        Assert.False(manager.IsActiveSymbol("AAPL"));
        Assert.False(manager.IsActiveSymbol("MSFT"));
    }

    private static IConfiguration CreateConfig(
        int maxLines = 95,
        int tickByTickMaxSymbols = 10,
        bool enableDepth = true,
        bool enableTape = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = maxLines.ToString(),
                ["MarketData:TickByTickMaxSymbols"] = tickByTickMaxSymbols.ToString(),
                ["MarketData:EnableDepth"] = enableDepth.ToString(),
                ["MarketData:EnableTape"] = enableTape.ToString(),
                ["MarketData:MinHoldMinutes"] = "0"
            })
            .Build();
        return config;
    }

    private static MarketDataSubscriptionManager CreateManager(IConfiguration config)
    {
        var classificationCache = new ContractClassificationCache(
            config,
            NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(
            config,
            NullLogger<ContractClassificationService>.Instance,
            classificationCache);
        var eligibilityCache = new DepthEligibilityCache(
            config,
            NullLogger<DepthEligibilityCache>.Instance);

        return new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);
    }
}
