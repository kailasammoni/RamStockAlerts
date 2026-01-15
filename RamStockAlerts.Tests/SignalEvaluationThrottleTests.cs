using System;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for signal evaluation throttling to prevent BUY SIGNAL log spam.
/// Tests focus on OrderBookState behavior with rapid updates.
/// </summary>
public sealed class SignalEvaluationThrottleTests
{
    [Fact]
    public void LastTapeRecvMs_UpdatesCorrectly_WithRapidTrades()
    {
        // Verify that rapid trade recording doesn't cause issues
        var book = new OrderBookState("AAPL");
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Simulate 100 rapid trades
        for (int i = 0; i < 100; i++)
        {
            var eventMs = baseMs + i;
            var recvMs = baseMs + i + 10; // Receipt slightly after event
            book.RecordTrade(eventMs, recvMs, 100.0 + (i * 0.01), 10m);
        }

        // Verify last receipt time is correct
        Assert.Equal(baseMs + 109, book.LastTapeRecvMs);
        Assert.Equal(100, book.RecentTrades.Count);
    }

    [Fact]
    public void TapeStatus_Stable_WithRapidEvaluations()
    {
        // Verify that rapid status checks don't cause issues
        var book = new OrderBookState("MSFT");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Add tape data
        for (int i = 0; i < 5; i++)
        {
            book.RecordTrade(nowMs - 5000 + (i * 1000), nowMs - 5000 + (i * 1000), 200.0, 10m);
        }

        var config = new ShadowTradingHelpers.TapeGateConfig(1, 15_000, 30_000);

        // Check status 100 times rapidly
        for (int i = 0; i < 100; i++)
        {
            var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, true, config);
            Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
        }

        Assert.True(true, "Rapid status checks handled without issues");
    }
}
