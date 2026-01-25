using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using Xunit;

namespace RamStockAlerts.Tests;

public class HardGateTests
{
    private static OrderFlowSignalValidator CreateValidator()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signals:HardGates:MaxSpoofScore"] = "0.3",
                ["Signals:HardGates:MinTapeAcceleration"] = "2.0",
                ["Signals:HardGates:MinWallPersistenceMs"] = "1000"
            })
            .Build();

        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        return new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
    }

    private static OrderFlowMetrics.MetricSnapshot CreateSnapshot(
        decimal spoofScore = 0.1m,
        decimal tapeAccel = 3.0m,
        long bidWallAgeMs = 1500,
        long askWallAgeMs = 1500)
    {
        return new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "AAPL",
            SpoofScore = spoofScore,
            TapeAcceleration = tapeAccel,
            BidWallAgeMs = bidWallAgeMs,
            AskWallAgeMs = askWallAgeMs
        };
    }

    [Fact]
    public void CheckHardGates_HighSpoofScore_Fails()
    {
        var validator = CreateValidator();
        var snapshot = CreateSnapshot(spoofScore: 0.5m);

        var result = validator.CheckHardGates(snapshot, isBuy: true);

        Assert.False(result.Passed);
        Assert.Equal("SpoofScore", result.FailedGate);
    }

    [Fact]
    public void CheckHardGates_LowTapeAccel_Fails()
    {
        var validator = CreateValidator();
        var snapshot = CreateSnapshot(tapeAccel: 1.5m);

        var result = validator.CheckHardGates(snapshot, isBuy: true);

        Assert.False(result.Passed);
        Assert.Equal("TapeAcceleration", result.FailedGate);
    }

    [Fact]
    public void CheckHardGates_ShortWallAge_Fails()
    {
        var validator = CreateValidator();
        var snapshot = CreateSnapshot(bidWallAgeMs: 500);

        var result = validator.CheckHardGates(snapshot, isBuy: true);

        Assert.False(result.Passed);
        Assert.Equal("WallPersistence", result.FailedGate);
    }

    [Fact]
    public void CheckHardGates_AllPass_Succeeds()
    {
        var validator = CreateValidator();
        var snapshot = CreateSnapshot();

        var result = validator.CheckHardGates(snapshot, isBuy: true);

        Assert.True(result.Passed);
    }
}
