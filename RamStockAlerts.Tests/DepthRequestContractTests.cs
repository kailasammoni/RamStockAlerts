using System;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services.Universe;
using Xunit;

namespace RamStockAlerts.Tests;

public class DepthRequestContractTests
{
    [Fact]
    public void BuildDepthContract_UsesSmartExchangeWhenPrimaryPresent()
    {
        var classification = new ContractClassification(
            "AAA",
            1,
            "STK",
            "SMART",
            "NASDAQ",
            "USD",
            "AAA",
            "AAA",
            null,
            null,
            "COMMON",
            DateTimeOffset.UtcNow);

        var contract = IBkrMarketDataClient.BuildDepthContractForDepth("AAA", classification);

        Assert.Equal("SMART", contract.Exchange);
        Assert.Equal("NASDAQ", contract.PrimaryExch);
    }

    [Fact]
    public void DepthRequestLogFields_CapturesContractDetails()
    {
        var contract = new IBApi.Contract
        {
            Symbol = "AAA",
            ConId = 123,
            SecType = "STK",
            Exchange = "NASDAQ",
            PrimaryExch = "NASDAQ",
            Currency = "USD",
            LocalSymbol = "AAA",
            TradingClass = "AAA",
            LastTradeDateOrContractMonth = "202601",
            Multiplier = "100"
        };

        var fields = IBkrMarketDataClient.BuildDepthRequestLogFields(
            contract,
            primaryExchange: "NASDAQ",
            depthRows: 5,
            isSmart: false);

        Assert.Equal("AAA", fields.Symbol);
        Assert.Equal(123, fields.ConId);
        Assert.Equal("STK", fields.SecType);
        Assert.Equal("NASDAQ", fields.Exchange);
        Assert.Equal("NASDAQ", fields.PrimaryExchange);
        Assert.Equal("USD", fields.Currency);
        Assert.Equal("AAA", fields.LocalSymbol);
        Assert.Equal("AAA", fields.TradingClass);
        Assert.Equal("202601", fields.LastTradeDateOrContractMonth);
        Assert.Equal("100", fields.Multiplier);
        Assert.Equal(5, fields.DepthRows);
        Assert.False(fields.IsSmart);
    }
}
