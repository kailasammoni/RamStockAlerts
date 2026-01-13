using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Microstructure;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class SpoofRejectTests
{
    [Fact]
    public void SpoofSuspected_WhenCancelDominantAndNoPrints()
    {
        var delta = new DepthDeltaSnapshot(
            new DepthDeltaWindowSnapshot(0, 3, 0, 0m, 30m, 0m, 3m),
            DepthDeltaWindowSnapshot.Empty,
            new DepthDeltaWindowSnapshot(0, 2, 0, 0m, 20m, 0m, 2m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var snapshot = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 0
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForSpoof("BUY", delta, snapshot, 0m);

        Assert.True(shouldReject);
    }

    [Fact]
    public void NoSpoof_WhenAddsBalanceCancels()
    {
        var delta = new DepthDeltaSnapshot(
            new DepthDeltaWindowSnapshot(2, 1, 0, 10m, 5m, 0m, 0.5m),
            DepthDeltaWindowSnapshot.Empty,
            new DepthDeltaWindowSnapshot(2, 2, 0, 10m, 10m, 0m, 1m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var snapshot = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 2
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForSpoof("BUY", delta, snapshot, 0m);

        Assert.False(shouldReject);
    }
}
