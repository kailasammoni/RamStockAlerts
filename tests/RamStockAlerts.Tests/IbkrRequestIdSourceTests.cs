using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Services;

namespace RamStockAlerts.Tests;

public class IbkrRequestIdSourceTests
{
    [Fact]
    public void NextId_IncrementsFromSeed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IBKR:RequestIdSeed"] = "500"
            })
            .Build();

        var source = new IbkrRequestIdSource(config);

        Assert.Equal(501, source.NextId());
        Assert.Equal(502, source.NextId());
    }
}
