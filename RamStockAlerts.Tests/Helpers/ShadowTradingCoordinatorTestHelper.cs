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
using RamStockAlerts.Tests.TestDoubles;

namespace RamStockAlerts.Tests.Helpers;

internal static class ShadowTradingCoordinatorTestHelper
{
    public static IConfiguration BuildConfig(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["TradingMode"] = "Shadow",
            ["MarketData:EnableDepth"] = "true",
            ["MarketData:EnableTape"] = "true",
            ["MarketData:MaxLines"] = "10",
            ["MarketData:TickByTickMaxSymbols"] = "1"
        };

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    public static (ShadowTradingCoordinator Coordinator, TestShadowTradeJournal Journal, OrderFlowMetrics Metrics) BuildCoordinator(
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

    public static async Task<MarketDataSubscriptionManager> CreateSubscriptionManagerAsync(
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

    public static OrderBookState BuildValidBook(string symbol, long nowMs)
    {
        var book = new OrderBookState(symbol);
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Bid, DepthOperation.Insert, 100.00m, 200m, 0, nowMs));
        book.ApplyDepthUpdate(new DepthUpdate(symbol, DepthSide.Ask, DepthOperation.Insert, 100.05m, 200m, 0, nowMs));
        return book;
    }
}
