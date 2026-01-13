using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class VwapReclaimTests
{
    [Fact]
    public void BuyReclaim_WhenPriceCrossesAboveVwapWithVolume()
    {
        var reclaim = ShadowTradingCoordinator.IsVwapReclaim(
            "BUY",
            10.5m,
            10m,
            9.9m,
            2m);

        Assert.True(reclaim);
    }

    [Fact]
    public void SellReclaim_WhenPriceCrossesBelowVwapWithVolume()
    {
        var reclaim = ShadowTradingCoordinator.IsVwapReclaim(
            "SELL",
            9.5m,
            10m,
            10.1m,
            2m);

        Assert.True(reclaim);
    }

    [Fact]
    public void NoReclaim_WhenVolumeLow()
    {
        var reclaim = ShadowTradingCoordinator.IsVwapReclaim(
            "BUY",
            10.5m,
            10m,
            9.9m,
            0.5m);

        Assert.False(reclaim);
    }
}
