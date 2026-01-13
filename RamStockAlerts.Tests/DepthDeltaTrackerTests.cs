using RamStockAlerts.Models;
using RamStockAlerts.Models.Microstructure;
using Xunit;

namespace RamStockAlerts.Tests;

public class DepthDeltaTrackerTests
{
    [Fact]
    public void InsertIncrementsAddCountWithinWindow()
    {
        var tracker = new DepthDeltaTracker();
        tracker.OnDepthUpdate(DepthSide.Bid, DepthOperation.Insert, 0, 10m, 50m, 0m, 1_000);

        var snapshot = tracker.GetSnapshot(1_500);

        Assert.Equal(1, snapshot.Bid1s.AddCount);
        Assert.Equal(50m, snapshot.Bid1s.TotalAddedSize);
    }

    [Fact]
    public void DeleteIncrementsCancelCount()
    {
        var tracker = new DepthDeltaTracker();
        tracker.OnDepthUpdate(DepthSide.Ask, DepthOperation.Delete, 0, 10.05m, 0m, 40m, 2_000);

        var snapshot = tracker.GetSnapshot(2_100);

        Assert.Equal(1, snapshot.Ask1s.CancelCount);
        Assert.Equal(40m, snapshot.Ask1s.TotalCanceledSize);
    }

    [Fact]
    public void UpdateTracksAbsSizeDelta()
    {
        var tracker = new DepthDeltaTracker();
        tracker.OnDepthUpdate(DepthSide.Bid, DepthOperation.Update, 0, 10m, 80m, 100m, 3_000);

        var snapshot = tracker.GetSnapshot(3_100);

        Assert.Equal(1, snapshot.Bid1s.UpdateCount);
        Assert.Equal(20m, snapshot.Bid1s.TotalAbsSizeDelta);
    }

    [Fact]
    public void EventsExpireAfterWindow()
    {
        var tracker = new DepthDeltaTracker();
        tracker.OnDepthUpdate(DepthSide.Bid, DepthOperation.Insert, 0, 10m, 10m, 0m, 100);

        var snapshot = tracker.GetSnapshot(2_000);

        Assert.Equal(0, snapshot.Bid1s.AddCount);
        Assert.Equal(0m, snapshot.Bid1s.TotalAddedSize);
    }
}
