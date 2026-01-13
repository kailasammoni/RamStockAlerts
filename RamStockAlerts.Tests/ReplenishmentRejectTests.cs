using RamStockAlerts.Engine;
using RamStockAlerts.Models.Decisions;
using RamStockAlerts.Models.Microstructure;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class ReplenishmentRejectTests
{
    [Fact]
    public void ReplenishmentSuspected_WhenAddsHighAndNoPrints_Buy()
    {
        var delta = new DepthDeltaSnapshot(
            DepthDeltaWindowSnapshot.Empty,
            new DepthDeltaWindowSnapshot(3, 1, 0, 30m, 5m, 0m, 0.33m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 0
        };
        var shouldReject = ShadowTradingCoordinator.ShouldRejectForReplenishment(
            "BUY",
            delta,
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 0m, null, null, null));

        Assert.True(shouldReject);
    }

    [Fact]
    public void NoReplenishment_WhenPrintsPresent_Buy()
    {
        var delta = new DepthDeltaSnapshot(
            DepthDeltaWindowSnapshot.Empty,
            new DepthDeltaWindowSnapshot(3, 1, 0, 30m, 5m, 0m, 0.33m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 2
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForReplenishment(
            "BUY",
            delta,
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 1m, null, null, null));

        Assert.False(shouldReject);
    }

    [Fact]
    public void ReplenishmentSuspected_SellSide()
    {
        var delta = new DepthDeltaSnapshot(
            new DepthDeltaWindowSnapshot(4, 1, 0, 40m, 5m, 0m, 0.25m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 0
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForReplenishment(
            "SELL",
            delta,
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 0m, null, null, null));

        Assert.True(shouldReject);
    }

    [Fact]
    public void NoReplenishment_WhenPrintsPresent_Sell()
    {
        var delta = new DepthDeltaSnapshot(
            new DepthDeltaWindowSnapshot(4, 1, 0, 40m, 5m, 0m, 0.25m),
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty,
            DepthDeltaWindowSnapshot.Empty);

        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 2
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForReplenishment(
            "SELL",
            delta,
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 1m, null, null, null));

        Assert.False(shouldReject);
    }

    [Fact]
    public void DecisionResult_PackagesReplenishmentFields()
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.Rejected,
            HardRejectReasons = new[] { HardRejectReason.ReplenishmentSuspected },
            BidAddCount1s = 2,
            AskAddCount1s = 3,
            BidTotalAddedSize1s = 15m,
            AskTotalAddedSize1s = 25m,
            BidCancelToAddRatio1s = 0.5m,
            AskCancelToAddRatio1s = 0.8m,
            TapeVolume3Sec = 0m,
            TradesIn3Sec = 0
        };

        var result = StrategyDecisionResultBuilder.Build(context);

        Assert.Contains(HardRejectReason.ReplenishmentSuspected, result.HardRejectReasons);
        Assert.Equal(3, result.Snapshot?.AskAddCount1s);
        Assert.Equal(25m, result.Snapshot?.AskTotalAddedSize1s);
        Assert.Equal(0.8m, result.Snapshot?.AskCancelToAddRatio1s);
        Assert.Equal(0, result.Snapshot?.TradesIn3Sec);
        Assert.Equal(0m, result.Snapshot?.TapeVolume3Sec);
    }
}
