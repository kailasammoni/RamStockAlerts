using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Tests;

public class ContractClassificationServiceTests
{
    [Fact]
    public void ResolveStockType_EmptyStockTypeReturnsUnknownEvenIfStk()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var cache = new ContractClassificationCache(config, NullLogger<ContractClassificationCache>.Instance);
        var service = new ContractClassificationService(config, NullLogger<ContractClassificationService>.Instance, cache);

        var classification = new ContractClassification("ABC", 1, "NASDAQ", "USD", "", DateTimeOffset.UtcNow);
        var mapped = service.Classify(classification);

        Assert.Equal(ContractSecurityClassification.Unknown, mapped);
    }
}
