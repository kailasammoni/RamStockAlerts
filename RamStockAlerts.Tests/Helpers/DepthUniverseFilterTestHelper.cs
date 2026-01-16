using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Tests.Helpers;

internal static class DepthUniverseFilterTestHelper
{
    public static (DepthUniverseFilter Filter, ContractClassificationCache Cache, DepthEligibilityCache Eligibility) BuildFilter()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var cache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var requestIdSource = new IbkrRequestIdSource(config);
        var service = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, cache, requestIdSource);
        var eligibility = new DepthEligibilityCache(config, NullLogger<DepthEligibilityCache>.Instance);
        var filter = new DepthUniverseFilter(service, config, NullLogger<DepthUniverseFilter>.Instance);
        return (filter, cache, eligibility);
    }
}
