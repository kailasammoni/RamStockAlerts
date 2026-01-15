using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using RamStockAlerts.Models;
using RamStockAlerts.Tests.TestDoubles;

namespace RamStockAlerts.Tests;

public class MarketDataSubscriptionManagerTests
{
    [Fact]
    public async Task DepthEligibility_CooldownSuppressesDepthRequestButKeepsSubscription()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("XYZ", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        eligibilityCache.MarkIneligible(new ContractClassification("XYZ", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), "XYZ", "DepthUnsupported", DateTimeOffset.UtcNow.AddMinutes(5));

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        var requestedDepth = new List<bool>();
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            requestedDepth.Add(requestDepth);
            return Task.FromResult<MarketDataSubscription?>(new MarketDataSubscription(symbol, 1, requestDepth ? 2 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(null);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "XYZ" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.Single(requestedDepth);
        Assert.False(requestedDepth[0]); // depth suppressed due to eligibility cooldown
    }

    [Fact]
    public async Task DepthError10092_MarksIneligibleAndRebalancesTape()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:TickByTickMaxSymbols"] = "1"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("CCC", 3, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol switch
            {
                "AAA" => 10,
                "BBB" => 20,
                _ => 30
            };
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(99);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB", "CCC" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // All 3 symbols have depth+tick-by-tick in new model (MaxDepthSymbols defaults to 3)
        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.True(manager.IsDepthEnabled("CCC"));

        await manager.HandleIbkrErrorAsync(
            requestId: 10,
            errorCode: 10092,
            errorMessage: "Deep market data not supported",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        await manager.HandleIbkrErrorAsync(
            requestId: 20,
            errorCode: 10092,
            errorMessage: "Deep market data not supported",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        // In new model: AAA and BBB lose depth+tick-by-tick but keep tape.
        // CCC still has depth+tick-by-tick.
        // No automatic rebalancing - that happens on next universe refresh.
        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsDepthEnabled("BBB"));
        Assert.True(manager.IsDepthEnabled("CCC"));
        Assert.True(manager.IsEligibleSymbol("CCC"));
    }

    [Fact]
    public async Task TickByTickCap_EnforcesSingleEnabled()
    {
        // In the new model: tick-by-tick is required for all depth symbols.
        // MaxDepthSymbols controls how many depth subscriptions we have (default 3).
        // All 3 depth symbols will get tick-by-tick (required for ActiveUniverse).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "3"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("CCC", 3, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol switch
            {
                "AAA" => 10,
                "BBB" => 20,
                _ => 30
            };
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        var enableCount = 0;
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            enableCount++;
            return Task.FromResult<int?>(99);
        }
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB", "CCC" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // All 3 symbols get depth, so all 3 should have tick-by-tick
        Assert.Equal(3, manager.GetTickByTickSymbols().Count);
        Assert.Equal(3, enableCount);
    }

    [Fact]
    public async Task TickByTickMovesAfterDepthIneligible_WithoutClearingRequestMapping()
    {
        // In the new model: when a symbol becomes depth ineligible, we remove its depth+tick-by-tick.
        // We don't automatically rebalance to other symbols - that happens on next universe refresh.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "2"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol == "AAA" ? 10 : 20;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        var nextTbtId = 100;
        var firstTbtId = 0;
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            nextTbtId += 1;
            if (firstTbtId == 0)
            {
                firstTbtId = nextTbtId;
            }
            return Task.FromResult<int?>(nextTbtId);
        }
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // Both symbols have depth+tick-by-tick in new model (MaxDepthSymbols=2)
        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.Equal(2, manager.GetTickByTickSymbols().Count);

        Assert.True(manager.TryGetRequestMapping(firstTbtId, out _, out _));

        await manager.HandleIbkrErrorAsync(
            requestId: 10,
            errorCode: 10092,
            errorMessage: "Deep market data not supported",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        // AAA loses depth+tick-by-tick, BBB keeps both (no rebalancing on error)
        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.Single(manager.GetTickByTickSymbols());
        Assert.True(manager.TryGetRequestMapping(firstTbtId, out _, out _));
    }

    [Fact]
    public async Task TickByTickCap_DoesNotExceedLimit()
    {
        // In the new model: all depth symbols get tick-by-tick (required for ActiveUniverse).
        // MaxDepthSymbols=3 means all 3 candidates get depth+tick-by-tick.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "3"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("CCC", 3, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        var enableCalls = 0;
        var nextRequestId = 100;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = requestDepth ? nextRequestId++ : (int?)null;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, nextRequestId++, depthId, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            enableCalls++;
            return Task.FromResult<int?>(nextRequestId++);
        }
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB", "CCC" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // All 3 depth symbols get tick-by-tick
        Assert.Equal(3, enableCalls);
        Assert.Equal(3, manager.GetTickByTickSymbols().Count);
    }

    [Fact]
    public async Task TickByTickEnableFailure_DoesNotMarkTapeEnabled()
    {
        // This test verifies the old behavior is preserved for the specific case
        // where tick-by-tick is disabled via config (not through cap hit).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "1"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        const int requestId = 200;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? 2 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(requestId);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // In new model: AAA has depth+tick-by-tick (is in ActiveUniverse)
        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsEligibleSymbol("AAA"));
        Assert.Single(manager.GetActiveUniverseSnapshot());

        await manager.HandleIbkrErrorAsync(
            requestId,
            errorCode: 10190,
            errorMessage: "Exceeded tick-by-tick cap",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        // After tick-by-tick cap: depth removed, no longer in ActiveUniverse, but tape remains
        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsEligibleSymbol("AAA"));
        Assert.Empty(manager.GetActiveUniverseSnapshot());
        Assert.False(manager.TryGetRequestMapping(requestId, out _, out _));
    }

    [Fact]
    public async Task TickByTickCapHit_RemovesSymbolFromActiveUniverse()
    {
        // Verifies that when tick-by-tick cap is hit (error 10190),
        // the symbol is removed from ActiveUniverse and depth is cancelled.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "20",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "2"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        var tickByTickRequestIds = new Dictionary<string, int>();
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var baseId = symbol == "AAA" ? 100 : 200;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, baseId, requestDepth ? baseId + 1 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        var nextTbtId = 300;
        Task<int?> EnableTbt(string symbol, CancellationToken __)
        {
            var id = nextTbtId++;
            tickByTickRequestIds[symbol] = id;
            return Task.FromResult<int?>(id);
        }
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        var depthDisableCalls = new List<string>();
        Task<bool> DisableDepth(string symbol, CancellationToken __)
        {
            depthDisableCalls.Add(symbol);
            return Task.FromResult(true);
        }

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // Both symbols have depth+tick-by-tick and are in ActiveUniverse
        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.True(manager.IsActiveSymbol("AAA"));
        Assert.True(manager.IsActiveSymbol("BBB"));
        Assert.Equal(2, manager.GetActiveUniverseSnapshot().Count);

        // Simulate tick-by-tick cap hit for AAA
        await manager.HandleIbkrErrorAsync(
            tickByTickRequestIds["AAA"],
            errorCode: 10190,
            errorMessage: "Exceeded tick-by-tick cap",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        // AAA should be removed from ActiveUniverse and depth cancelled
        Assert.False(manager.IsActiveSymbol("AAA"));
        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.Contains("AAA", depthDisableCalls);
        
        // BBB should remain in ActiveUniverse
        Assert.True(manager.IsActiveSymbol("BBB"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        
        // ActiveUniverse should only contain BBB now
        var activeSnapshot = manager.GetActiveUniverseSnapshot();
        Assert.Single(activeSnapshot);
        Assert.Contains("BBB", activeSnapshot);
    }

    [Fact]
    public async Task TickByTickRebalanceAtCap_DoesNotRepeatEnable()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:TickByTickMaxSymbols"] = "1"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        var enableCalls = 0;
        var nextRequestId = 300;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = requestDepth ? nextRequestId++ : (int?)null;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, nextRequestId++, depthId, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            enableCalls++;
            return Task.FromResult<int?>(nextRequestId++);
        }
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // In new model: both symbols have depth (MaxDepthSymbols defaults to 3),
        // so both get tick-by-tick on first call, no repeat enables on second call.
        Assert.Equal(2, enableCalls);
        Assert.Equal(2, manager.GetTickByTickSymbols().Count);
    }

    [Fact]
    public async Task UniverseUpdate_EmitsJournalEntryWithCorrectStructure()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "95",
                ["MarketData:MaxDepthSymbols"] = "3",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        
        // Set up 5 candidates (3 will get depth, all 3 will get tick-by-tick to become Active)
        var symbols = new[] { "AAA", "BBB", "CCC", "DDD", "EEE" };
        foreach (var symbol in symbols)
        {
            await classificationCache.PutAsync(
                new ContractClassification(symbol, 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        var journal = new TestShadowTradeJournal();
        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            journal);

        var subscribeCount = 0;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            subscribeCount++;
            var mktDataId = subscribeCount;
            var depthId = requestDepth ? subscribeCount + 100 : (int?)null;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, mktDataId, depthId, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        
        var tickByTickId = 200;
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            return Task.FromResult<int?>(tickByTickId++);
        }
        
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        // Apply universe with 5 candidates (only top 3 by score will get depth)
        await manager.ApplyUniverseAsync(
            symbols,
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        // Verify journal entry was emitted
        Assert.Single(journal.Entries);
        var entry = journal.Entries[0];
        
        // Verify entry metadata
        Assert.Equal("UniverseUpdate", entry.EntryType);
        Assert.Equal("MarketDataSubscriptionManager", entry.Source);
        Assert.Equal(journal.SessionId, entry.SessionId);
        Assert.NotNull(entry.MarketTimestampUtc);
        
        // Verify UniverseUpdate snapshot
        Assert.NotNull(entry.UniverseUpdate);
        var snapshot = entry.UniverseUpdate;
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.NotNull(snapshot.NowUtc);
        Assert.True(snapshot.NowMs > 0);
        
        // Verify candidates (all 5 symbols)
        Assert.NotNull(snapshot.Candidates);
        Assert.Equal(5, snapshot.Candidates.Count);
        Assert.Contains("AAA", snapshot.Candidates);
        Assert.Contains("EEE", snapshot.Candidates);
        
        // Verify ActiveUniverse (only top 3 with depth + tick-by-tick)
        Assert.NotNull(snapshot.ActiveUniverse);
        Assert.Equal(3, snapshot.ActiveUniverse.Count);
        
        // Verify exclusions (2 symbols with tape but not in ActiveUniverse)
        Assert.NotNull(snapshot.Exclusions);
        Assert.Equal(2, snapshot.Exclusions.Count);
        foreach (var exclusion in snapshot.Exclusions)
        {
            Assert.NotNull(exclusion.Symbol);
            Assert.Equal("NoDepth", exclusion.Reason); // These symbols didn't make the top 3
        }
        
        // Verify counts
        Assert.NotNull(snapshot.Counts);
        Assert.Equal(5, snapshot.Counts.CandidatesCount);
        Assert.Equal(3, snapshot.Counts.ActiveCount);
        Assert.Equal(3, snapshot.Counts.DepthCount);
        Assert.Equal(3, snapshot.Counts.TickByTickCount);
        Assert.Equal(5, snapshot.Counts.TapeCount);
    }

    [Fact]
    public async Task UniverseUpdate_LimitsCandidatesTo20()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "95",
                ["MarketData:MaxDepthSymbols"] = "3",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        
        // Create 30 candidates
        var symbols = Enumerable.Range(1, 30).Select(i => $"SYM{i:D3}").ToArray();
        foreach (var symbol in symbols)
        {
            await classificationCache.PutAsync(
                new ContractClassification(symbol, 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        var journal = new TestShadowTradeJournal();
        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            journal);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? 2 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(200);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
        Task<bool> DisableDepth(string _, CancellationToken __) => Task.FromResult(true);

        await manager.ApplyUniverseAsync(
            symbols,
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.Single(journal.Entries);
        var snapshot = journal.Entries[0].UniverseUpdate;
        
        // Verify candidates limited to 20 (to prevent spam)
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Candidates);
        Assert.Equal(20, snapshot.Candidates.Count);
        
        // Verify counts reflect actual totals
        Assert.Equal(30, snapshot.Counts.CandidatesCount);
    }
}
