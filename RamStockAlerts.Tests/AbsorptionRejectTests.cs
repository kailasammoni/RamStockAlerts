using RamStockAlerts.Engine;
using RamStockAlerts.Models.Decisions;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class AbsorptionRejectTests
{
    [Fact]
    public void AbsorptionInsufficient_WhenNoTrades_Buy()
    {
        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 0
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForAbsorption(
            "BUY",
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 0m, null, null, null));

        Assert.True(shouldReject);
    }

    [Fact]
    public void NoAbsorptionReject_WhenTradesPresent_Buy()
    {
        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 3
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForAbsorption(
            "BUY",
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 2m, null, null, null));

        Assert.False(shouldReject);
    }

    [Fact]
    public void AbsorptionInsufficient_WhenNoTrades_Sell()
    {
        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 0
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForAbsorption(
            "SELL",
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 0m, null, null, null));

        Assert.True(shouldReject);
    }

    [Fact]
    public void NoAbsorptionReject_WhenTradesPresent_Sell()
    {
        var metrics = new OrderFlowMetrics.MetricSnapshot
        {
            Symbol = "TEST",
            TradesIn3Sec = 2
        };

        var shouldReject = ShadowTradingCoordinator.ShouldRejectForAbsorption(
            "SELL",
            metrics,
            new ShadowTradingCoordinator.TapeStats(0m, 1m, null, null, null));

        Assert.False(shouldReject);
    }
}
