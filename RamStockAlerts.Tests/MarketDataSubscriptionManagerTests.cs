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
}
