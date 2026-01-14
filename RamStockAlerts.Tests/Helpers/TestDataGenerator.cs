using System;
using RamStockAlerts.Models;

namespace RamStockAlerts.Tests.Helpers;

/// <summary>
/// Generates test data for development and testing.
/// </summary>
public static class TestDataGenerator
{
    public static TradeSignal GeneratePerfectSetup(string ticker)
    {
        return new TradeSignal
        {
            Ticker = ticker,
            Entry = 100m,
            Stop = 99m,
            Target = 103m,
            Score = 9.5m,
            Timestamp = DateTime.UtcNow
        };
    }

    public static TradeSignal GenerateMarginalSetup(string ticker)
    {
        return new TradeSignal
        {
            Ticker = ticker,
            Entry = 50m,
            Stop = 49.50m,
            Target = 51m,
            Score = 7.6m,
            Timestamp = DateTime.UtcNow
        };
    }

    public static (OrderBook, TapeData, VwapData) GeneratePerfectMarketConditions()
    {
        var orderBook = new OrderBook
        {
            BidAskRatio = 4m,
            TotalBidSize = 10000m,
            TotalAskSize = 2500m,
            Spread = 0.02m,
            Timestamp = DateTime.UtcNow
        };

        var tapeData = new TapeData
        {
            PrintsPerSecond = 8m,
            LastPrintSize = 500m,
            Timestamp = DateTime.UtcNow
        };

        var vwapData = new VwapData
        {
            CurrentPrice = 100.50m,
            VwapPrice = 100.00m,
            HasReclaim = true
        };

        return (orderBook, tapeData, vwapData);
    }

    public static (OrderBook, TapeData, VwapData) GenerateWeakMarketConditions()
    {
        var orderBook = new OrderBook
        {
            BidAskRatio = 0.5m,
            TotalBidSize = 1000m,
            TotalAskSize = 2000m,
            Spread = 0.08m,
            Timestamp = DateTime.UtcNow
        };

        var tapeData = new TapeData
        {
            PrintsPerSecond = 2m,
            LastPrintSize = 100m,
            Timestamp = DateTime.UtcNow
        };

        var vwapData = new VwapData
        {
            CurrentPrice = 99.00m,
            VwapPrice = 100.00m,
            HasReclaim = false
        };

        return (orderBook, tapeData, vwapData);
    }
}
