using RamStockAlerts.Models;
using Xunit;

namespace RamStockAlerts.Tests;

public class OrderBookStateDepthDeltaTests
{
    [Fact]
    public void DeleteByShape_IsTrackedAsCancel()
    {
        var book = new OrderBookState("TEST");
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 10.00m, 50m, 0, 1_000));

        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Update, 0m, 0m, 0, 1_100));

        var snapshot = book.DepthDeltaTracker.GetSnapshot(1_200);

        Assert.Equal(1, snapshot.Bid1s.CancelCount);
        Assert.Equal(0, snapshot.Bid1s.AddCount);
    }
}
