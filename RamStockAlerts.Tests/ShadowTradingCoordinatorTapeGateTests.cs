using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using RamStockAlerts.Models;
using RamStockAlerts.Tests.Helpers;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradingCoordinatorTapeGateTests
{
    [Fact]
    public async Task TapeMissingSubscription_RejectsWithFlagsAndTrace()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig();
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: false);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Rejected", entry.DecisionOutcome);
        // Tape-only symbols (with mktData but no tick-by-tick) now pass tape check but fail on NoDepth
        Assert.Equal("NotReady_NoDepth", entry.RejectionReason);
        Assert.Contains("GateReject:NotReady_NoDepth", entry.DecisionTrace ?? new List<string>());
        Assert.Null(entry.DecisionInputs);
    }

    [Fact]
    public async Task TapeNotWarmedUp_RejectsWithFlagsAndTrace()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig();
        var symbol = "TEST";
        var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: true);
        var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Rejected", entry.DecisionOutcome);
        Assert.Equal("NotReady_TapeNotWarmedUp", entry.RejectionReason);
        Assert.Contains("GateReject:NotReady_TapeNotWarmedUp", entry.DecisionTrace ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp", entry.DataQualityFlags ?? new List<string>());
        Assert.Null(entry.DecisionInputs);
    }

    [Fact]
    public async Task GatingRejection_SuppressesSameReasonWithinInterval()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:GateRejectLogMinIntervalMs"] = "0",
            ["ShadowTrading:GatingRejectSuppressionSeconds"] = "5"
        });
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: false);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);
        coordinator.ProcessSnapshot(book, nowMs + 1);

        var entry = Assert.Single(journal.Entries);
        // Tape-only symbols now fail on NoDepth instead of TapeMissingSubscription
        Assert.Equal("NotReady_NoDepth", entry.RejectionReason);
    }

    [Fact]
    public async Task GatingRejection_LogsWhenReasonChanges()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:GateRejectLogMinIntervalMs"] = "0",
            ["ShadowTrading:GatingRejectSuppressionSeconds"] = "5"
        });
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: false);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var invalidBook = new OrderBookState(symbol);

        coordinator.ProcessSnapshot(invalidBook, nowMs);

        // Allow enough time to bypass evaluation throttle (250ms)
        var validBook = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs + 300);
        metrics.UpdateMetrics(validBook, nowMs + 300);
        coordinator.ProcessSnapshot(validBook, nowMs + 300);

        Assert.Equal(2, journal.Entries.Count);
        Assert.Contains(journal.Entries, entry => entry.RejectionReason == "NotReady_BookInvalid");
        // Tape-only symbols now fail on NoDepth instead of TapeMissingSubscription
        Assert.Contains(journal.Entries, entry => entry.RejectionReason == "NotReady_NoDepth");
    }

    [Fact]
    public async Task GateReject_NotWarmedUp_WritesOnceWithinInterval()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:GateRejectLogMinIntervalMs"] = "2000",
            ["ShadowTrading:GatingRejectSuppressionSeconds"] = "5"
        });
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: true);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        for (var i = 0; i < 5; i++)
        {
            coordinator.ProcessSnapshot(book, nowMs);
        }

        Assert.Single(journal.Entries);
        Assert.Equal("NotReady_TapeNotWarmedUp", journal.Entries[0].RejectionReason);
    }

    [Fact]
    public async Task GateReject_NotWarmedUp_EmitsDiagnosticsFlags()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:TapeWarmupMinTrades"] = "1",
            ["MarketData:TapeWarmupWindowMs"] = "15000",
            ["MarketData:GateRejectLogMinIntervalMs"] = "0",
            ["ShadowTrading:GatingRejectSuppressionSeconds"] = "5"
        });
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: true);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("NotReady_TapeNotWarmedUp", entry.RejectionReason);
        Assert.Contains("TapeNotWarmedUp:tradesInWindow=0", entry.DataQualityFlags ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp:warmupMinTrades=1", entry.DataQualityFlags ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp:warmupWindowMs=15000", entry.DataQualityFlags ?? new List<string>());

        book.RecordTrade(nowMs - 1000, nowMs - 1000, 100.10, 1m);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        Assert.Equal(1, journal.Entries.Count(e => e.RejectionReason == "NotReady_TapeNotWarmedUp"));
    }

    [Fact]
    public async Task GatingRejection_AllowsNewEntryAfterSuppressionWindow()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:GateRejectLogMinIntervalMs"] = "0",
            ["ShadowTrading:GatingRejectSuppressionSeconds"] = "0.01"
        });
        var symbol = "TEST";
            var (manager, sharedMetrics) = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerWithMetricsAsync(config, symbol, enableTickByTick: false);
            var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager, sharedMetrics);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);
        await Task.Delay(TimeSpan.FromMilliseconds(25));
        coordinator.ProcessSnapshot(book, nowMs + 1);

        Assert.Equal(2, journal.Entries.Count);
        Assert.All(journal.Entries, entry => Assert.Equal("NotReady_TapeMissingSubscription", entry.RejectionReason));
    }

}
