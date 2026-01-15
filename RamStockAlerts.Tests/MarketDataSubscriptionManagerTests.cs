using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;

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

        Assert.True(manager.IsDepthEnabled("AAA"));
        Assert.True(manager.IsTapeEnabled("AAA"));
        Assert.False(manager.IsTapeEnabled("BBB"));

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
    }

    [Fact]
    public async Task TickByTickCap_EnforcesSingleEnabled()
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

        Assert.Equal(1, manager.GetTickByTickSymbols().Count);
        Assert.Equal(1, enableCount);
    }

    [Fact]
    public async Task TickByTickMovesAfterDepthIneligible_WithoutClearingRequestMapping()
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

        Assert.True(manager.IsTapeEnabled("AAA"));
        Assert.False(manager.IsTapeEnabled("BBB"));

        Assert.True(manager.TryGetRequestMapping(firstTbtId, out _, out _));

        await manager.HandleIbkrErrorAsync(
            requestId: 10,
            errorCode: 10092,
            errorMessage: "Deep market data not supported",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        Assert.False(manager.IsTapeEnabled("AAA"));
        Assert.True(manager.IsTapeEnabled("BBB"));
        Assert.True(manager.TryGetRequestMapping(firstTbtId, out _, out _));
    }

    [Fact]
    public async Task TickByTickCap_DoesNotExceedLimit()
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

        Assert.Equal(1, enableCalls);
        Assert.Single(manager.GetTickByTickSymbols());
    }

    [Fact]
    public async Task TickByTickEnableFailure_DoesNotMarkTapeEnabled()
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

        Assert.True(manager.IsTapeEnabled("AAA"));

        await manager.HandleIbkrErrorAsync(
            requestId,
            errorCode: 10190,
            errorMessage: "Exceeded tick-by-tick cap",
            disableDepthAsync: DisableDepth,
            disableTickByTickAsync: DisableTbt,
            enableTickByTickAsync: EnableTbt,
            cancellationToken: CancellationToken.None);

        Assert.False(manager.IsTapeEnabled("AAA"));
        Assert.False(manager.TryGetRequestMapping(requestId, out _, out _));
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

        Assert.Equal(1, enableCalls);
        Assert.Single(manager.GetTickByTickSymbols());
    }
}
