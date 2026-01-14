using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
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
        var manager = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: false);
        var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Rejected", entry.DecisionOutcome);
        Assert.Equal("NotReady_TapeMissingSubscription", entry.RejectionReason);
        Assert.Contains("GateReject:NotReady_TapeMissingSubscription", entry.DecisionTrace ?? new List<string>());
        Assert.Contains("TapeMissingSubscription", entry.DataQualityFlags ?? new List<string>());
        Assert.Null(entry.DecisionInputs);
    }

    [Fact]
    public async Task TapeNotWarmedUp_RejectsWithFlagsAndTrace()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig();
        var symbol = "TEST";
        var manager = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: true);
        var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager);
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
    public async Task GateReject_NotWarmedUp_WritesOnceWithinInterval()
    {
        var config = ShadowTradingCoordinatorTestHelper.BuildConfig(new Dictionary<string, string?>
        {
            ["MarketData:GateRejectLogMinIntervalMs"] = "2000"
        });
        var symbol = "TEST";
        var manager = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: true);
        var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager);
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
            ["MarketData:GateRejectLogMinIntervalMs"] = "0"
        });
        var symbol = "TEST";
        var manager = await ShadowTradingCoordinatorTestHelper.CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: true);
        var (coordinator, journal, metrics) = ShadowTradingCoordinatorTestHelper.BuildCoordinator(config, manager);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = ShadowTradingCoordinatorTestHelper.BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("NotReady_TapeNotWarmedUp", entry.RejectionReason);
        Assert.Contains("TapeNotWarmedUp:tradesInWindow=0", entry.DataQualityFlags ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp:warmupMinTrades=1", entry.DataQualityFlags ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp:warmupWindowMs=15000", entry.DataQualityFlags ?? new List<string>());

        book.RecordTrade(nowMs - 1000, 100.10, 1m);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        Assert.Equal(1, journal.Entries.Count(e => e.RejectionReason == "NotReady_TapeNotWarmedUp"));
    }

}
