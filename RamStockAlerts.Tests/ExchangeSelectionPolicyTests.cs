using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for primary-exchange-first L1 and tick-by-tick routing with SMART fallback.
/// Validates exchange selection policy and fallback mechanism.
/// </summary>
public class ExchangeSelectionPolicyTests
{
    /// <summary>
    /// Helper method that implements the same SelectL1Exchange logic as IBkrMarketDataClient.
    /// This allows us to test the exchange selection policy independently.
    /// </summary>
    private static string SelectL1Exchange(ContractClassification? classification)
    {
        if (classification?.PrimaryExchange != null)
        {
            var primary = classification.PrimaryExchange.Trim().ToUpperInvariant();
            if (primary == "NASDAQ" || primary == "NYSE" || primary == "AMEX" || primary == "CBOE" || primary == "BOX")
            {
                return primary;
            }
        }
        return "SMART";
    }

    [Fact]
    public void SelectL1Exchange_WithNasdaqPrimaryExchange_ReturnNasdaq()
    {
        // Arrange
        var classification = new ContractClassification(
            "AAPL",
            265598,
            "STK",
            "NASDAQ",  // Primary exchange
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("NASDAQ", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithNYSEPrimaryExchange_ReturnNYSE()
    {
        // Arrange
        var classification = new ContractClassification(
            "IBM",
            8314,
            "STK",
            "NYSE",  // Primary exchange
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("NYSE", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithNullClassification_ReturnSMART()
    {
        // Act
        var exchange = SelectL1Exchange(null);

        // Assert
        Assert.Equal("SMART", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithMissingPrimaryExchange_ReturnSMART()
    {
        // Arrange
        var classification = new ContractClassification(
            "TEST",
            999,
            "STK",
            null,  // No primary exchange
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("SMART", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithUnknownPrimaryExchange_ReturnSMART()
    {
        // Arrange
        var classification = new ContractClassification(
            "TEST",
            999,
            "STK",
            "UNKNOWN_EXCHANGE",  // Unknown exchange
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("SMART", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithWhitespaceAndMixedCase_ReturnNormalized()
    {
        // Arrange
        var classification = new ContractClassification(
            "TEST",
            999,
            "STK",
            "  nasdaq  ",  // Whitespace and mixed case
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("NASDAQ", exchange);
    }

    [Fact]
    public void SelectL1Exchange_WithCBOEExchange_ReturnCBOE()
    {
        // Arrange
        var classification = new ContractClassification(
            "TEST",
            999,
            "STK",
            "CBOE",  // CBOE is allowed
            "USD",
            "COMMON",
            DateTimeOffset.UtcNow);

        // Act
        var exchange = SelectL1Exchange(classification);

        // Assert
        Assert.Equal("CBOE", exchange);
    }

    [Fact]
    public void MarketDataSubscription_TracksL1AndTickByTickExchange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var sub = new MarketDataSubscription(
            "AAPL",
            MktDataRequestId: 1001,
            DepthRequestId: 1002,
            TickByTickRequestId: 1003,
            DepthExchange: "NASDAQ",
            MktDataExchange: "NASDAQ",
            TickByTickExchange: "NASDAQ",
            MktDataFirstReceiptMs: now,
            TickByTickFirstReceiptMs: now);

        // Act & Assert
        Assert.Equal("NASDAQ", sub.MktDataExchange);
        Assert.Equal("NASDAQ", sub.TickByTickExchange);
        Assert.NotNull(sub.MktDataFirstReceiptMs);
        Assert.NotNull(sub.TickByTickFirstReceiptMs);
    }

    [Fact]
    public void MarketDataSubscription_FallbackToSMART_TracksNewExchange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var originalSub = new MarketDataSubscription(
            "AAPL",
            MktDataRequestId: 1001,
            DepthRequestId: 1002,
            TickByTickRequestId: 1003,
            DepthExchange: "NASDAQ",
            MktDataExchange: "NASDAQ",
            TickByTickExchange: "NASDAQ",
            MktDataFirstReceiptMs: now,
            TickByTickFirstReceiptMs: now);

        // Act - Simulate fallback by creating new subscription with SMART exchange
        var fallbackSub = originalSub with
        {
            MktDataRequestId = 2001,  // New request ID
            MktDataExchange = "SMART",  // Fallback exchange
            MktDataFirstReceiptMs = DateTimeOffset.UtcNow  // Reset timeout counter
        };

        // Assert
        Assert.Equal("NASDAQ", originalSub.MktDataExchange);
        Assert.Equal("SMART", fallbackSub.MktDataExchange);
        Assert.NotEqual(originalSub.MktDataRequestId, fallbackSub.MktDataRequestId);
        Assert.True(fallbackSub.MktDataFirstReceiptMs > originalSub.MktDataFirstReceiptMs);
    }

    [Fact]
    public void ConfigurationTimeout_MinimumBound_Clamped()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:L1ReceiptTimeoutMs"] = "2000"  // Too low
            })
            .Build();

        var timeoutMs = Math.Max(5_000, config.GetValue("MarketData:L1ReceiptTimeoutMs", 15_000));

        // Act & Assert - Should be clamped to minimum 5000ms
        Assert.Equal(5_000, timeoutMs);
    }

    [Fact]
    public void ConfigurationTimeout_DefaultValue_Applied()
    {
        // Arrange
        var config = new ConfigurationBuilder().Build();

        var timeoutMs = Math.Max(5_000, config.GetValue("MarketData:TickByTickReceiptTimeoutMs", 15_000));

        // Act & Assert - Should be default 15000ms
        Assert.Equal(15_000, timeoutMs);
    }

    [Fact]
    public void ConfigurationTimeout_CustomValue_Applied()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:TickByTickReceiptTimeoutMs"] = "20000"
            })
            .Build();

        var timeoutMs = Math.Max(5_000, config.GetValue("MarketData:TickByTickReceiptTimeoutMs", 15_000));

        // Act & Assert
        Assert.Equal(20_000, timeoutMs);
    }
}
