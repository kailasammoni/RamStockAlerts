using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradingCoordinatorTapeGateTests
{
    [Fact]
    public async Task TapeMissingSubscription_RejectsWithFlagsAndTrace()
    {
        var config = BuildConfig();
        var symbol = "TEST";
        var manager = await CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: false);
        var (coordinator, journal, metrics) = BuildCoordinator(config, manager);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = BuildValidBook(symbol, nowMs);
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
        var config = BuildConfig();
        var symbol = "TEST";
        var manager = await CreateSubscriptionManagerAsync(config, symbol, enableTickByTick: true);
        var (coordinator, journal, metrics) = BuildCoordinator(config, manager);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = BuildValidBook(symbol, nowMs);
        metrics.UpdateMetrics(book, nowMs);

        coordinator.ProcessSnapshot(book, nowMs);

        var entry = Assert.Single(journal.Entries);
        Assert.Equal("Rejected", entry.DecisionOutcome);
        Assert.Equal("NotReady_TapeNotWarmedUp", entry.RejectionReason);
        Assert.Contains("GateReject:NotReady_TapeNotWarmedUp", entry.DecisionTrace ?? new List<string>());
        Assert.Contains("TapeNotWarmedUp", entry.DataQualityFlags ?? new List<string>());
        Assert.Null(entry.DecisionInputs);
    }

    private static IConfiguration BuildConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TradingMode"] = "Shadow",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxLines"] = "10",
                ["MarketData:TickByTickMaxSymbols"] = "1"
            })
            .Build();
    }

    private static (ShadowTradingCoordinator Coordinator, TestShadowTradeJournal Journal, OrderFlowMetrics Metrics) BuildCoordinator(
        IConfiguration config,
        MarketDataSubscriptionManager manager)
    {
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics);
        var journal = new TestShadowTradeJournal();
        var scarcity = new ScarcityController(config);
        var coordinator = new ShadowTradingCoordinator(
            config,
            metrics,
            validator,
            journal,
            scarcity,
            manager,
            NullLogger<ShadowTradingCoordinator>.Instance);

        return (coordinator, journal, metrics);
    }

    private static async Task<MarketDataSubscriptionManager> CreateSubscriptionManagerAsync(
        IConfiguration config,
        string symbol,
        bool enableTickByTick)
    {
        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(
            new ContractClassification(symbol, 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string s, bool requestDepth, CancellationToken token)
        {
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(s, 1, requestDepth ? 2 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(enableTickByTick ? 3 : null);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { symbol },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        return manager;
    }

    private static OrderBookState BuildValidBook(string symbol, long nowMs)
    {
        var book = new OrderBookState(symbol);
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, 100.00m, 200m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, 100.05m, 200m, 0, nowMs));
        return book;
    }

    private sealed class TestShadowTradeJournal : IShadowTradeJournal
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public List<ShadowTradeJournalEntry> Entries { get; } = new();

        public bool TryEnqueue(ShadowTradeJournalEntry entry)
        {
            Entries.Add(entry);
            return true;
        }
    }
}
