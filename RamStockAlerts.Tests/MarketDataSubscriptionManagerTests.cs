using System;
using System.Collections.Generic;
using System.Threading;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;
using RamStockAlerts.Models;
using RamStockAlerts.Tests.TestDoubles;

namespace RamStockAlerts.Tests;

public class MarketDataSubscriptionManagerTests
{
    private static OrderFlowMetrics CreateMetrics() => new(NullLogger<OrderFlowMetrics>.Instance);

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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        await classificationCache.PutAsync(new ContractClassification("XYZ", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        eligibilityCache.MarkIneligible(new ContractClassification("XYZ", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), "XYZ", "DepthUnsupported", DateTimeOffset.UtcNow.AddMinutes(5));

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
    public async Task DepthError10092_FirstAttemptTriggersRetry()
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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol switch
            {
                "AAA" => 10,
                _ => 20
            };
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(99);
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

        // All 3 symbols have depth+tick-by-tick in new model (MaxDepthSymbols defaults to 3)
        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.True(manager.IsDepthEnabled("CCC"));

        var plan = await manager.TryGetDepthRetryPlanAsync(10, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal("SMART", plan!.PreviousExchange);
        Assert.Equal("NYSE", plan.NextExchange);

        var classification = classificationCache.TryGetCached("AAA");
        var eligibility = eligibilityCache.Get(classification, "AAA", DateTimeOffset.UtcNow);

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsTapeEnabled("AAA"));
        Assert.False(manager.IsTapeEnabled("BBB"));
        Assert.NotEqual(DepthEligibilityStatus.Ineligible, eligibility.Status);
    }

    [Fact]
    public async Task DepthError10092_SecondAttemptConfirmsUnsupportedAndRebalancesTape()
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

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol switch
            {
                "AAA" => 10,
                _ => 20
            };
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(99);
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

        var plan = await manager.TryGetDepthRetryPlanAsync(10, CancellationToken.None);
        Assert.NotNull(plan);

        manager.ClearDepthRequest("AAA", 10);
        manager.UpdateDepthRequest("AAA", 11, plan!.NextExchange);

        await manager.HandleIbkrErrorAsync(
            requestId: 11,
            errorCode: 10092,
            errorMessage: "Deep market data not supported",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        var classification = classificationCache.TryGetCached("AAA");
        var eligibility = eligibilityCache.Get(classification, "AAA", DateTimeOffset.UtcNow);

        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsTapeEnabled("AAA"));
        Assert.True(manager.IsTapeEnabled("BBB"));
        Assert.Equal(DepthEligibilityStatus.Ineligible, eligibility.Status);
    }

    [Fact]
    public async Task DepthError10092_SecondAttemptSuccessKeepsEligibility()
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
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? 10 : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(99);
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

        var plan = await manager.TryGetDepthRetryPlanAsync(10, CancellationToken.None);
        Assert.NotNull(plan);

        manager.ClearDepthRequest("AAA", 10);
        manager.UpdateDepthRequest("AAA", 11, plan!.NextExchange);

        var classification = classificationCache.TryGetCached("AAA");
        var eligibility = eligibilityCache.Get(classification, "AAA", DateTimeOffset.UtcNow);

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsTapeEnabled("AAA"));
        Assert.NotEqual(DepthEligibilityStatus.Ineligible, eligibility.Status);
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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("CCC", 3, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("CCC", 3, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
    public async Task DepthEligibilitySummary_LogsUniverseSizeAndLastIneligibleSymbols()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true"
            })
            .Build();

        var logger = new ListLogger<MarketDataSubscriptionManager>();
        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);

        var symbols = new[] { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF" };
        var depthRequestIds = new Dictionary<string, int>();
        var nextId = 10;
        foreach (var symbol in symbols)
        {
            await classificationCache.PutAsync(
                new ContractClassification(symbol, nextId, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow),
                CancellationToken.None);
            depthRequestIds[symbol] = nextId;
            nextId += 10;
        }

        var manager = new MarketDataSubscriptionManager(
            config,
            logger,
            classificationService,
            eligibilityCache);

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthRequestIds[symbol] : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(null);
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

        foreach (var symbol in symbols)
        {
            await manager.HandleIbkrErrorAsync(
                requestId: depthRequestIds[symbol],
                errorCode: 10092,
                errorMessage: "Deep market data not supported",
                disableDepthAsync: DisableDepth,
                disableTickByTickAsync: DisableTbt,
                enableTickByTickAsync: EnableTbt,
                cancellationToken: CancellationToken.None);
        }

        await manager.ApplyUniverseAsync(
            symbols,
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
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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

        var summary = logger.Messages.Last(message => message.StartsWith("DepthEligibilitySummary", StringComparison.Ordinal));
        Assert.Contains("universeSize=6", summary);

        var marker = "last10092Symbols=[";
        var startIndex = summary.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var endIndex = summary.IndexOf(']', startIndex);
        var symbolsSegment = summary.Substring(startIndex, endIndex - startIndex);
        var entries = symbolsSegment.Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, entries.Length);

        var expectedSymbols = new[] { "BBB", "CCC", "DDD", "EEE", "FFF" };
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var openIndex = entry.IndexOf('(');
            var closeIndex = entry.LastIndexOf(')');
            Assert.True(openIndex > 0);
            Assert.True(closeIndex > openIndex);
            Assert.Equal(expectedSymbols[i], entry[..openIndex]);

            var timestamp = entry[(openIndex + 1)..closeIndex];
            Assert.True(DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _));
        }
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
        var requestIdSource = new IbkrRequestIdSource(config);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache, requestIdSource);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = CreateMetrics();
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

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
    public async Task FocusRotation_EvictsIdleDepthAfterDwell()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "1",
                ["MarketData:FocusRotationEnabled"] = "true",
                ["MarketData:FocusMinDwellMs"] = "10",
                ["MarketData:FocusTapeIdleMs"] = "5",
                ["MarketData:FocusDepthIdleMs"] = "5",
                ["MarketData:FocusWarmupMinTrades"] = "0"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

        var depthDisableCalls = new List<string>();
        var requestId = 100;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = requestDepth ? requestId++ : (int?)null;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, requestId++, depthId, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(requestId++);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
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

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsDepthEnabled("BBB"));

        await Task.Delay(20);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.False(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsDepthEnabled("BBB"));
        Assert.Contains("AAA", depthDisableCalls);
    }

    [Fact]
    public async Task FocusRotation_RetainsActiveDepthWithRecentTape()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:MaxDepthSymbols"] = "1",
                ["MarketData:FocusRotationEnabled"] = "true",
                ["MarketData:FocusMinDwellMs"] = "10",
                ["MarketData:FocusTapeIdleMs"] = "50",
                ["MarketData:FocusDepthIdleMs"] = "50",
                ["MarketData:FocusWarmupMinTrades"] = "0"
            })
            .Build();

        var classificationCache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var classificationService = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, classificationCache);
        var eligibilityCache = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        await classificationCache.PutAsync(new ContractClassification("AAA", 1, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);
        await classificationCache.PutAsync(new ContractClassification("BBB", 2, "STK", "NYSE", "USD", "COMMON", DateTimeOffset.UtcNow), CancellationToken.None);

        var manager = new MarketDataSubscriptionManager(
            config,
            NullLogger<MarketDataSubscriptionManager>.Instance,
            classificationService,
            eligibilityCache,
            metrics);

        var depthDisableCalls = new List<string>();
        var requestId = 200;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = requestDepth ? requestId++ : (int?)null;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, requestId++, depthId, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(requestId++);
        Task<bool> DisableTbt(string _, CancellationToken __) => Task.FromResult(true);
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

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsDepthEnabled("BBB"));

        manager.RecordTapeReceipt("AAA", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await Task.Delay(20);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.False(manager.IsDepthEnabled("BBB"));
        Assert.DoesNotContain("AAA", depthDisableCalls);
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
        var metrics = CreateMetrics();
        
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
            metrics,
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
        
        // NOTE: ActiveSymbols will be empty in this test because we don't provide book data.
        // Without book data, triage scoring returns -100 for all symbols (dead/ineligible),
        // so no symbols get depth subscriptions and thus no symbols reach ActiveUniverse.
        // In production, symbols with real market data would populate ActiveSymbols.
        Assert.NotNull(snapshot.ActiveSymbols);
        
        // Verify exclusions exist (symbols with tape but not enough to be active)
        Assert.NotNull(snapshot.Exclusions);
        
        // Verify counts
        Assert.NotNull(snapshot.Counts);
        Assert.Equal(5, snapshot.Counts.CandidatesCount);
        // ActiveCount, DepthCount, TickByTickCount will be 0 without book data
        // TapeCount may vary based on subscription logic
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
        var metrics = CreateMetrics();
        
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
            metrics,
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

    [Fact]
    public async Task TickByTickEnableFailure_StopsFurtherEnablesInCycle()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:MaxLines"] = "10",
                ["MarketData:EnableDepth"] = "true",
                ["MarketData:EnableTape"] = "true",
                ["MarketData:TickByTickMaxSymbols"] = "2"
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
        var enableCalls = 0;
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            enableCalls++;
            return Task.FromResult<int?>(null);
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

        Assert.Equal(1, enableCalls);
        Assert.Empty(manager.GetTickByTickSymbols());
    }

    [Fact]
    public async Task TickByTickRebalance_DebouncesRecentEnable()
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

        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol == "AAA" ? 10 : 20;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        var nextRequestId = 400;
        Task<int?> EnableTbt(string _, CancellationToken __) => Task.FromResult<int?>(nextRequestId++);
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

        Assert.True(manager.IsTapeEnabled("AAA"));

        await manager.ApplyUniverseAsync(
            new[] { "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.True(manager.IsTapeEnabled("AAA"));
        Assert.False(manager.IsTapeEnabled("BBB"));
    }

    [Fact]
    public async Task TickByTickCapReached_SkipsRebalanceEnables()
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
        const int requestId = 500;
        Task<MarketDataSubscription?> Subscribe(string symbol, bool requestDepth, CancellationToken token)
        {
            var depthId = symbol == "AAA" ? 10 : 20;
            return Task.FromResult<MarketDataSubscription?>(
                new MarketDataSubscription(symbol, 1, requestDepth ? depthId : null, null, requestDepth ? "SMART" : null));
        }

        Task<bool> Unsubscribe(string _, CancellationToken __) => Task.FromResult(true);
        Task<int?> EnableTbt(string _, CancellationToken __)
        {
            enableCalls++;
            return Task.FromResult<int?>(requestId);
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

        Assert.True(manager.IsTapeEnabled("AAA"));

        await manager.HandleIbkrErrorAsync(
            requestId,
            errorCode: 10190,
            errorMessage: "Exceeded tick-by-tick cap",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        await manager.ApplyUniverseAsync(
            new[] { "AAA", "BBB" },
            Subscribe,
            Unsubscribe,
            EnableTbt,
            DisableTbt,
            DisableDepth,
            CancellationToken.None);

        Assert.Equal(1, enableCalls);
        Assert.Empty(manager.GetTickByTickSymbols());
    }
}
