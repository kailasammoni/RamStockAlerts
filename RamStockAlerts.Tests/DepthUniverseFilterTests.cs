using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        var filter = new DepthUniverseFilter(service, config, NullLogger<DepthUniverseFilter>.Instance);
        return (filter, cache, eligibility);
    }

    [Fact]
    public async Task DepthUniverseFilter_RemovesEtfStockType()
    {
        var (filter, cache, _) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        await cache.PutAsync(new ContractClassification("AAA", 1, "NASDAQ", "USD", "COMMON", now), CancellationToken.None);
        await cache.PutAsync(new ContractClassification("ETF1", 2, "ARCA", "USD", "ETF", now), CancellationToken.None);

        var filteredResult = await filter.FilterAsync(new[] { "AAA", "ETF1" }, CancellationToken.None);
        var filtered = filteredResult.Filtered;

        Assert.Contains("AAA", filtered);
        Assert.DoesNotContain("ETF1", filtered);
        Assert.Equal(1, filteredResult.EtfCount);
        Assert.Equal(2, filteredResult.RawCount);
    }

    [Fact]
    public async Task DepthUniverseFilter_DoesNotRemoveDepthIneligible()
    {
        var (filter, cache, eligibility) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        var classification = new ContractClassification("XYZ", 10, "NYSE", "USD", "COMMON", now);
        await cache.PutAsync(classification, CancellationToken.None);
        eligibility.MarkIneligible(classification, "XYZ", "DepthUnsupported", now.AddMinutes(5));

        var filteredResult = await filter.FilterAsync(new[] { "XYZ" }, CancellationToken.None);
        var filtered = filteredResult.Filtered;

        Assert.Contains("XYZ", filtered);
        Assert.Equal(0, filteredResult.EtfCount);
        Assert.Equal(1, filteredResult.RawCount);
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

    [Fact]
    public void Eligibility_MarksEligibleWithNullCooldown()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var eligibility = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var now = DateTimeOffset.UtcNow;

        var classification = new ContractClassification("ELIG", 0, null, "USD", "COMMON", now);
        eligibility.MarkEligible(classification, "ELIG");

        var state = eligibility.Get(classification, "ELIG", now);
        Assert.Equal(DepthEligibilityStatus.Eligible, state.Status);
        Assert.Null(state.CooldownUntil);
    }

    [Fact]
    public async Task UnknownExcludedByDefaultIncludedWithOverride()
    {
        var configDefault = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var cache = new ContractClassificationCache(configDefault, NullLogger<ContractClassificationCache>.Instance);
        var service = new ContractClassificationService(configDefault, NullLogger<ContractClassificationService>.Instance, cache);
        var filterDefault = new DepthUniverseFilter(service, configDefault, NullLogger<DepthUniverseFilter>.Instance);

        var now = DateTimeOffset.UtcNow;
        await cache.PutAsync(new ContractClassification("UNK", 1, "NYSE", "USD", "UNKNOWN", now), CancellationToken.None);

        var resultDefault = await filterDefault.FilterAsync(new[] { "UNK" }, CancellationToken.None);
        Assert.Empty(resultDefault.Filtered);

        var configOverride = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Universe:AllowUnknownAsCommon"] = "true"
            })
            .Build();
        var cache2 = new ContractClassificationCache(configOverride, NullLogger<ContractClassificationCache>.Instance);
        var service2 = new ContractClassificationService(configOverride, NullLogger<ContractClassificationService>.Instance, cache2);
        await cache2.PutAsync(new ContractClassification("UNK", 1, "NYSE", "USD", "UNKNOWN", now), CancellationToken.None);
        var filterOverride = new DepthUniverseFilter(service2, configOverride, NullLogger<DepthUniverseFilter>.Instance);

        var resultOverride = await filterOverride.FilterAsync(new[] { "UNK" }, CancellationToken.None);
        Assert.Contains("UNK", resultOverride.Filtered);
    }

    [Fact]
    public async Task DepthUniverseFilter_ExcludesNonCommonStockType()
    {
        var (filter, cache, _) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        await cache.PutAsync(new ContractClassification("TSLL", 2, "NASDAQ", "USD", "ETF", now), CancellationToken.None);

        var result = await filter.FilterAsync(new[] { "TSLL" }, CancellationToken.None);

        Assert.Empty(result.Filtered);
        Assert.Equal(1, result.EtfCount);
    }

    [Fact]
    public async Task DepthUniverseFilter_ExcludesMissingPrimaryExchange()
    {
        var (filter, cache, _) = BuildFilter();
        var now = DateTimeOffset.UtcNow;
        await cache.PutAsync(new ContractClassification("NOEX", 3, null, "USD", "COMMON", now), CancellationToken.None);

        var result = await filter.FilterAsync(new[] { "NOEX" }, CancellationToken.None);

        Assert.Empty(result.Filtered);
        Assert.Equal(1, result.CommonCount);
    }

    [Fact]
    public void DepthEligibility_PersistsAndLoadsWithinTtl()
    {
        var path = Path.GetTempFileName();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketData:DepthEligibilityCacheFile"] = path,
                    ["MarketData:DepthEligibilityTtlHours"] = "24"
                })
                .Build();

            var eligibility = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
            var now = DateTimeOffset.UtcNow;
            var classification = new ContractClassification("PERSIST", 456, "NYSE", "USD", "COMMON", now);
            eligibility.MarkIneligible(classification, "PERSIST", "DepthUnsupported", now.AddMinutes(5));

            var reloaded = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
            var state = reloaded.Get(classification, "PERSIST", DateTimeOffset.UtcNow.AddMinutes(1));

            Assert.Equal(DepthEligibilityStatus.Ineligible, state.Status);
            Assert.Equal(456, state.ConId);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void DepthEligibilityCache_CooldownBlocksRequests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var logger = new ListLogger<DepthEligibilityCache>();
        var eligibility = new DepthEligibilityCache(config, logger);
        var now = DateTimeOffset.UtcNow;
        var classification = new ContractClassification("COOLDOWN", 123, "NYSE", "USD", "COMMON", now);

        eligibility.MarkIneligible(classification, "COOLDOWN", "DepthUnsupported", now.AddMinutes(10));

        var canRequestDuring = eligibility.CanRequestDepth(classification, "COOLDOWN", now.AddMinutes(5), out var stateDuring);
        Assert.False(canRequestDuring);
        eligibility.LogSkipOnce(classification, "COOLDOWN", stateDuring);
        eligibility.LogSkipOnce(classification, "COOLDOWN", stateDuring);
        Assert.Equal(1, logger.Count);

        var canRequestAfter = eligibility.CanRequestDepth(classification, "COOLDOWN", now.AddMinutes(11), out _);
        Assert.True(canRequestAfter);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        public int Count => _messages.Count;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
