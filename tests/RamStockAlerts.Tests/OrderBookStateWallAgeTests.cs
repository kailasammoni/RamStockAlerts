using RamStockAlerts.Models;
using Xunit;

namespace RamStockAlerts.Tests;

public class OrderBookStateWallAgeTests
{
    [Fact]
    public void BestBidAge_DoesNotReset_OnSizeOnlyUpdate()
    {
        var book = new OrderBookState("TEST");
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 10.00m, 100m, 0, 1_000));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Update, 10.00m, 120m, 0, 1_500));

        Assert.Equal(500, book.BestBidAgeMs);
    }

    [Fact]
    public void BestBidAge_Resets_OnBestPriceChange()
    {
        var book = new OrderBookState("TEST");
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 10.00m, 100m, 0, 1_000));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Update, 10.05m, 100m, 0, 2_000));

        Assert.Equal(0, book.BestBidAgeMs);
    }

    [Fact]
    public void BestBidAge_Resets_WhenBookEmpties()
    {
        var book = new OrderBookState("TEST");
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 10.00m, 100m, 0, 1_000));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Delete, 10.00m, 0m, 0, 1_100));

        Assert.Equal(long.MaxValue, book.BestBidAgeMs);
    }

    [Fact]
    public void AllowsDeleteWithZeroPrice()
    {
        var book = new OrderBookState("TEST");
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 10.00m, 100m, 0, 1_000));

        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Delete, 0m, 0m, 0, 1_100));

        Assert.Equal(0m, book.BestBid);
    }
}
