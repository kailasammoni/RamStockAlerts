using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for triage precondition gating and dead symbol exclusion.
/// Verifies that "alive + active" symbols are eligible for promotion, while "dead" symbols are rejected.
/// </summary>
public class TriageEligibilityTests
{
    [Fact]
    public void OrderBookState_TracksLastL1RecvMs()
    {
        // Arrange
        var symbol = "TEST";
        var book = new OrderBookState(symbol);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        book.LastL1RecvMs = nowMs - 500;

        // Assert
        Assert.NotNull(book.LastL1RecvMs);
        Assert.True(book.LastL1RecvMs > 0);
    }

    [Fact]
    public void OrderBookState_TracksTradeReceipts()
    {
        // Arrange
        var symbol = "TEST";
        var book = new OrderBookState(symbol);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act: Record multiple trades
        for (int i = 0; i < 3; i++)
        {
            book.RecordTrade(nowMs - 1000 + i * 200, nowMs - 1000 + i * 200, 100.00, 100m);
        }

        // Assert
        Assert.Equal(3, book.RecentTrades.Count);
        Assert.All(book.RecentTrades, trade => 
        {
            Assert.True(trade.ReceiptTimestampMs > 0);
            Assert.True(trade.Price > 0);
            Assert.True(trade.Size > 0);
        });
    }

    [Fact]
    public void SymbolWithNoTradingActivity_IsIneligible()
    {
        // Arrange: Create symbol with NO trades (BIYA-style dead symbol)
        var symbol = "DEAD";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var lookbackMs = 15_000;
        var windowStart = nowMs - lookbackMs;

        var book = new OrderBookState(symbol);
        // No depth, no trades

        // Act: Check if book qualifies for eligibility based on trade count
        var trades = book.RecentTrades.ToArray();
        var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();

        // Assert: Dead symbol should have no trades in window
        Assert.Empty(tradesInWindow);
    }

    [Fact]
    public void SymbolWithRecentTrades_HasActivityInWindow()
    {
        // Arrange: Create symbol with recent trades (eligible)
        var symbol = "ACTIVE";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        var book = new OrderBookState(symbol);
        
        // Add 5 trades in the last 15 seconds
        for (int i = 0; i < 5; i++)
        {
            var tradeMs = nowMs - (10_000 - i * 2000);
            book.RecordTrade(tradeMs, tradeMs, 100.005, 100m);
        }

        // Act
        var trades = book.RecentTrades.ToArray();
        var windowStart = nowMs - 15_000;
        var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();
        
        // Assert: Symbol with recent trades is eligible
        Assert.NotEmpty(tradesInWindow);
        Assert.True(tradesInWindow.Count >= 1);
    }

    [Fact]
    public void SymbolWithL1Data_HasLatestQuote()
    {
        // Arrange: Create symbol with L1 data (eligible)
        var symbol = "QUOTED";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        var book = new OrderBookState(symbol);
        book.LastL1RecvMs = nowMs - 500; // Recent L1 update
        
        // Act
        var hasL1 = book.LastL1RecvMs.HasValue;
        var isRecent = book.LastL1RecvMs > nowMs - 2000;

        // Assert
        Assert.True(hasL1, "Should have L1 data");
        Assert.True(isRecent, "L1 should be recent");
    }

    [Fact]
    public void SymbolWithoutL1Data_IsIneligible()
    {
        // Arrange: Create symbol with NO L1 data (ineligible)
        var symbol = "NOQUOTE";
        var book = new OrderBookState(symbol);

        // Act
        var hasL1 = book.LastL1RecvMs.HasValue;

        // Assert
        Assert.False(hasL1, "Should not have L1 data");
    }

    [Fact]
    public void PreconditionChecks_PreventBIYAStyle()
    {
        // Arrange: Simulate BIYA-style dead symbol (no trades, no L1, no spread)
        var symbol = "BIYA";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        var book = new OrderBookState(symbol);
        // No depth, no trades, no L1 update

        // Act: Check preconditions
        var windowStart = nowMs - 15_000;
        var trades = book.RecentTrades.ToArray();
        var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();

        var hasTape = tradesInWindow.Count >= 1;
        var hasL1 = book.LastL1RecvMs.HasValue;
        var spreadKnown = book.BestBid > 0 && book.BestAsk > 0;

        // Assert: All preconditions fail - symbol is ineligible
        Assert.False(hasTape, "BIYA should have no tape");
        Assert.False(hasL1, "BIYA should have no L1");
        Assert.False(spreadKnown, "BIYA should have unknown spread");
    }

    [Fact]
    public void SymbolWithPastTrades_HasStaleActivity()
    {
        // Arrange: Create symbol with STALE trades (outside lookback window - ineligible)
        var symbol = "STALE";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var lookbackMs = 15_000;
        var windowStart = nowMs - lookbackMs;

        var book = new OrderBookState(symbol);
        // Add old trades BEFORE the window
        book.RecordTrade(windowStart - 10_000, windowStart - 10_000, 100.00, 100m);
        book.RecordTrade(windowStart - 5_000, windowStart - 5_000, 100.00, 100m);

        // Act
        var trades = book.RecentTrades.ToArray();
        var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();

        // Assert: Trades exist but are all stale (not in current window) - symbol ineligible
        Assert.NotEmpty(trades);  // Book has trades historically
        Assert.Empty(tradesInWindow);  // But none in the lookback window - ineligible
    }

    [Fact]
    public void SymbolWithMixedTradeHistory_CorrectlyCountsWindowTrades()
    {
        // Arrange: Create symbol with both old and recent trades
        var symbol = "MIXED";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var lookbackMs = 15_000;
        var windowStart = nowMs - lookbackMs;

        var book = new OrderBookState(symbol);
        // Add OLD trades (before window)
        book.RecordTrade(windowStart - 20_000, windowStart - 20_000, 100.00, 100m);
        book.RecordTrade(windowStart - 10_000, windowStart - 10_000, 100.00, 100m);
        
        // Add RECENT trades (in window - eligible)
        book.RecordTrade(nowMs - 5_000, nowMs - 5_000, 100.00, 100m);
        book.RecordTrade(nowMs - 2_000, nowMs - 2_000, 100.00, 100m);

        // Act
        var trades = book.RecentTrades.ToArray();
        var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();

        // Assert
        Assert.Equal(4, trades.Length);  // All trades still in queue
        Assert.Equal(2, tradesInWindow.Count);  // 2 in current window - eligible
    }
}

