using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Tests;

public class DepthUniverseFilterTests
{
    private static (DepthUniverseFilter Filter, ContractClassificationCache Cache, DepthEligibilityCache Eligibility) BuildFilter()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var cache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var service = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, cache);
        var eligibility = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var filter = new DepthUniverseFilter(service, eligibility, NullLogger<DepthUniverseFilter>.Instance);
        return (filter, cache, eligibility);
    }

    [Fact]
    public async Task Filter_RemovesEtfClassifications()
    {
        var (filter, cache, _) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        await cache.PutAsync(new ContractClassification("AAA", 1, "NASDAQ", "USD", "COMMON", now), CancellationToken.None);
        await cache.PutAsync(new ContractClassification("ETF1", 2, "ARCA", "USD", "ETF", now), CancellationToken.None);

        var filtered = await filter.FilterAsync(new[] { "AAA", "ETF1" }, CancellationToken.None);

        Assert.Contains("AAA", filtered);
        Assert.DoesNotContain("ETF1", filtered);
    }

    [Fact]
    public async Task Filter_SkipsIneligibleDuringCooldown()
    {
        var (filter, cache, eligibility) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        var classification = new ContractClassification("XYZ", 0, null, "USD", "COMMON", now);
        await cache.PutAsync(classification, CancellationToken.None);
        eligibility.MarkIneligible(classification, "XYZ", "DepthUnsupported", now.AddMinutes(10));

        var filtered = await filter.FilterAsync(new[] { "XYZ" }, CancellationToken.None);

        Assert.Empty(filtered);
    }

    [Fact]
    public void Eligibility_UsesConIdKeyWhenAvailable()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var eligibility = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var now = DateTimeOffset.UtcNow;

        var classification = new ContractClassification("ABC", 123, "NASDAQ", "USD", "COMMON", now);
        eligibility.MarkIneligible(classification, "ABC", "DepthUnsupported", now.AddHours(1));

        var lookup = new ContractClassification("ABC", 123, "NYSE", "USD", "COMMON", now);
        var state = eligibility.Get(lookup, "ABC", now);

        Assert.Equal(DepthEligibilityStatus.Ineligible, state.Status);
        Assert.Equal(123, state.ConId);

        var fallback = new ContractClassification("DEF", 0, "NYSE", "USD", "COMMON", now);
        eligibility.MarkIneligible(fallback, "DEF", "DepthUnsupported", now.AddHours(1));
        var fallbackState = eligibility.Get(fallback, "DEF", now);

        Assert.Equal(DepthEligibilityStatus.Ineligible, fallbackState.Status);
    }
}
