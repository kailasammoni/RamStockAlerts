using RamStockAlerts.Models.Microstructure;
using Xunit;

namespace RamStockAlerts.Tests;

public class VwapTrackerTests
{
    [Fact]
    public void TracksCumulativeVwap()
    {
        var tracker = new VwapTracker();
        tracker.OnTrade(100, 1m, 1);
        tracker.OnTrade(101, 2m, 2);

        Assert.Equal(3m, tracker.CumVolume);
        Assert.Equal(100.666666666666666666666666667m, tracker.CurrentVwap);
    }
}
