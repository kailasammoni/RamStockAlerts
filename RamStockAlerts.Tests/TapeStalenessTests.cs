using Microsoft.Extensions.Configuration;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Unit tests for tape staleness calculation in ShadowTradingHelpers.GetTapeStatus.
/// Tests boundary conditions: just-fresh, just-stale, negative age (clock skew).
/// </summary>
public class TapeStalenessTests
{
    [Fact]
    public void GetTapeStatus_FreshTape_ReturnsReady()
    {
        // Arrange: last trade 1 second ago (threshold is 5 seconds)
        var book = CreateBookWithTrade(tradeTimestampMs: 1000000000L);
        var nowMs = 1000001000L; // 1 second later
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
        Assert.Equal(1000, status.AgeMs); // 1 second
    }

    [Fact]
    public void GetTapeStatus_JustStale_ExactlyAtThreshold_ReturnsStale()
    {
        // Arrange: last trade exactly 5001ms ago (threshold is 5000ms)
        var tradeTimestampMs = 1000000000L;
        var book = CreateBookWithTrade(tradeTimestampMs);
        var nowMs = tradeTimestampMs + 5001;
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Stale, status.Kind);
        Assert.Equal(5001, status.AgeMs);
    }

    [Fact]
    public void GetTapeStatus_JustFresh_OneLessThanThreshold_ReturnsReady()
    {
        // Arrange: last trade 4999ms ago (just under 5000ms threshold)
        var tradeTimestampMs = 1000000000L;
        var book = CreateBookWithTrade(tradeTimestampMs);
        var nowMs = tradeTimestampMs + 4999;
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
        Assert.Equal(4999, status.AgeMs);
    }

    [Fact]
    public void GetTapeStatus_VeryStale_ReturnsStaleWithDiagnostics()
    {
        // Arrange: last trade 60 seconds ago
        var tradeTimestampMs = 1000000000L;
        var book = CreateBookWithTrade(tradeTimestampMs);
        var nowMs = tradeTimestampMs + 60000;
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Stale, status.Kind);
        Assert.Equal(60000, status.AgeMs);
    }

    [Fact]
    public void GetTapeStatus_NegativeAge_ClockSkew_NotConsideredStale()
    {
        // Arrange: trade timestamp is in the future (clock skew)
        var nowMs = 1000000000L;
        var tradeTimestampMs = nowMs + 1000; // 1 second in the future
        var book = CreateBookWithTrade(tradeTimestampMs);
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        // Negative age (-1000ms) should not be stale (age > staleWindow check: -1000 > 5000 = false)
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
        Assert.Equal(-1000, status.AgeMs);
    }

    [Fact]
    public void GetTapeStatus_NoTrades_ReturnsNotWarmedUp()
    {
        // Arrange: book with no trades
        var book = new OrderBookState("TEST")
        {
            MaxDepthRows = 10
        };
        var nowMs = 1000000000L;
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.NotWarmedUp, status.Kind);
    }

    [Fact]
    public void GetTapeStatus_TapeDisabled_ReturnsMissingSubscription()
    {
        // Arrange
        var book = CreateBookWithTrade(1000000000L);
        var nowMs = 1000001000L;
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 1, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: false, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.MissingSubscription, status.Kind);
    }

    [Fact]
    public void GetTapeStatus_InsufficientWarmupTrades_ReturnsNotWarmedUp()
    {
        // Arrange: only 2 trades but warmup requires 5
        var book = new OrderBookState("TEST")
        {
            MaxDepthRows = 10
        };
        var nowMs = 1000010000L;
        book.RecordTrade(nowMs - 5000, nowMs - 5000, 100.0, 10m);
        book.RecordTrade(nowMs - 3000, nowMs - 3000, 100.5, 15m);
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 5, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.NotWarmedUp, status.Kind);
        Assert.Equal(2, status.TradesInWarmupWindow);
        Assert.Equal(5, status.WarmupMinTrades);
    }

    [Fact]
    public void GetTapeStatus_StalenessCheckBeforeWarmup_ReturnsStalePriority()
    {
        // Arrange: 5 trades but last trade is stale (> 5 seconds old)
        var book = new OrderBookState("TEST")
        {
            MaxDepthRows = 10
        };
        var nowMs = 1000020000L;
        book.RecordTrade(nowMs - 15000, nowMs - 15000, 100.0, 10m);
        book.RecordTrade(nowMs - 14000, nowMs - 14000, 100.1, 10m);
        book.RecordTrade(nowMs - 13000, nowMs - 13000, 100.2, 10m);
        book.RecordTrade(nowMs - 12000, nowMs - 12000, 100.3, 10m);
        book.RecordTrade(nowMs - 11000, nowMs - 11000, 100.4, 10m);
        var config = CreateConfig(staleWindowMs: 5000, warmupMinTrades: 3, warmupWindowMs: 10000);

        // Act
        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Assert
        // Staleness check happens first, so should return Stale even though warmup condition is also failed
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Stale, status.Kind);
        Assert.Equal(11000, status.AgeMs); // Age of last trade
    }

    private static OrderBookState CreateBookWithTrade(long tradeTimestampMs)
    {
        var book = new OrderBookState("TEST")
        {
            MaxDepthRows = 10
        };
        book.RecordTrade(tradeTimestampMs, tradeTimestampMs, 100.0, 10m);
        return book;
    }

    private static ShadowTradingHelpers.TapeGateConfig CreateConfig(int staleWindowMs, int warmupMinTrades, int warmupWindowMs)
    {
        return new ShadowTradingHelpers.TapeGateConfig(
            StaleWindowMs: staleWindowMs,
            WarmupMinTrades: warmupMinTrades,
            WarmupWindowMs: warmupWindowMs);
    }
}
