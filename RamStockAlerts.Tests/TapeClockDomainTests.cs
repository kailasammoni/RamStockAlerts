using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Tests for clock-domain separation between IB event timestamps and local receipt timestamps.
/// These tests verify that tape staleness and warmup gating use receipt time, not event time,
/// which can lag due to IB server delays, batching, or replay scenarios.
/// </summary>
public sealed class TapeClockDomainTests
{
    [Fact]
    public void TapeReady_WhenEventTimeLagsBehindReceiptTime_ButReceiptTimeIsCurrent()
    {
        // Scenario: IB timestamps are 60s behind real time, but trades are flowing (receipt time updating)
        // Expected: Tape should be Ready, not Stale
        var book = new OrderBookState("AAPL");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Simulate 5 trades with IB timestamps 60s behind, but receipt times are current
        for (int i = 0; i < 5; i++)
        {
            var eventMs = nowMs - 60_000 + (i * 100); // Event time 60s behind
            var recvMs = nowMs - 1000 + (i * 100);     // Receipt time 1s ago, flowing
            book.RecordTrade(eventMs, recvMs, 100.0 + i * 0.01, 10m);
        }

        var config = new ShadowTradingHelpers.TapeGateConfig(
            WarmupMinTrades: 1,
            WarmupWindowMs: 15_000,
            StaleWindowMs: 30_000);

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Key assertion: Should be Ready because receipt time is current
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
        Assert.True(status.AgeMs < 1500, $"Expected age < 1500ms, got {status.AgeMs}ms");
    }

    [Fact]
    public void TapeStale_WhenReceiptTimeIsOld_RegardlessOfEventTime()
    {
        // Scenario: Receipt time is old (no new trades arriving)
        // Expected: Tape should be Stale, even if we have trade data
        var book = new OrderBookState("MSFT");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Last trade received 45 seconds ago
        var eventMs = nowMs - 45_000;
        var recvMs = nowMs - 45_000; // Receipt time is also old
        book.RecordTrade(eventMs, recvMs, 100.0, 10m);

        var config = new ShadowTradingHelpers.TapeGateConfig(
            WarmupMinTrades: 1,
            WarmupWindowMs: 15_000,
            StaleWindowMs: 30_000);

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Key assertion: Should be Stale because receipt time is >30s old
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Stale, status.Kind);
        Assert.True(status.AgeMs > 30_000, $"Expected age > 30s, got {status.AgeMs}ms");
    }

    [Fact]
    public void WarmupPasses_WithEnoughTradesInReceiptTimeWindow_EvenIfEventTimeSkewed()
    {
        // Scenario: 5 trades arrived within warmup window (receipt time), but event times are skewed
        // Expected: Warmup passes based on receipt time count
        var book = new OrderBookState("NVDA");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // 5 trades with receipt times in last 10s, but event times scattered
        for (int i = 0; i < 5; i++)
        {
            var eventMs = nowMs - 120_000 + (i * 10_000); // Event times 120s ago, spread out
            var recvMs = nowMs - 10_000 + (i * 2000);     // Receipt times in last 10s
            book.RecordTrade(eventMs, recvMs, 200.0 + i * 0.1, 10m);
        }

        var config = new ShadowTradingHelpers.TapeGateConfig(
            WarmupMinTrades: 5,
            WarmupWindowMs: 15_000,
            StaleWindowMs: 30_000);

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        // Key assertion: Warmup passes because we have 5 trades (counting by event time in window)
        // Note: Trade counting still uses event time for warmup, but staleness uses receipt time
        Assert.True(status.TradesInWarmupWindow >= 5, $"Expected >= 5 trades, got {status.TradesInWarmupWindow}");
        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
    }

    [Fact]
    public void LastTapeRecvMs_UpdatesOnEachTrade()
    {
        // Verify that LastTapeRecvMs is correctly tracked
        var book = new OrderBookState("TSLA");
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        Assert.Equal(0, book.LastTapeRecvMs); // Initially zero

        book.RecordTrade(baseMs - 1000, baseMs, 100.0, 10m);
        Assert.Equal(baseMs, book.LastTapeRecvMs);

        book.RecordTrade(baseMs - 500, baseMs + 1000, 100.1, 10m);
        Assert.Equal(baseMs + 1000, book.LastTapeRecvMs);

        book.RecordTrade(baseMs, baseMs + 2000, 100.2, 10m);
        Assert.Equal(baseMs + 2000, book.LastTapeRecvMs);
    }

    [Fact]
    public void LastTapeRecvMs_MonotonicallyIncreasing()
    {
        // Verify that LastTapeRecvMs only moves forward
        var book = new OrderBookState("AMD");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        book.RecordTrade(nowMs - 1000, nowMs, 100.0, 10m);
        var recv1 = book.LastTapeRecvMs;
        
        // Attempt to record with older receipt time (shouldn't decrease)
        book.RecordTrade(nowMs - 500, nowMs - 100, 100.1, 10m);
        Assert.Equal(recv1, book.LastTapeRecvMs); // Should stay at max value

        // Record with newer receipt time
        book.RecordTrade(nowMs, nowMs + 1000, 100.2, 10m);
        Assert.Equal(nowMs + 1000, book.LastTapeRecvMs);
    }
}
