using RamStockAlerts.Models;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Unit tests for staleness age calculation in OrderBookState.
/// Tests boundary conditions: just-fresh, just-stale, negative age (clock skew), and zero-timestamp edge cases.
/// </summary>
public class OrderBookStalenessTests
{
    [Fact]
    public void IsBookValid_FreshDepth_ReturnsValid()
    {
        // Arrange: depth updated 1 second ago (threshold is 2000ms)
        var book = CreateValidBook();
        var nowMs = 1000000000L;
        var depthUpdateMs = nowMs - 1000; // 1 second old
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, depthUpdateMs));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, depthUpdateMs));

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        Assert.True(isValid, $"Expected valid but got reason: {reason}");
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void IsBookValid_JustStale_ExactlyAtThreshold_ReturnsInvalid()
    {
        // Arrange: depth updated exactly 2001ms ago (threshold is 2000ms)
        var book = CreateValidBook();
        var nowMs = 1000000000L;
        var depthUpdateMs = nowMs - 2001; // 2001ms old (just over threshold)
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, depthUpdateMs));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, depthUpdateMs));

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        Assert.False(isValid);
        Assert.StartsWith("StaleDepthData", reason);
        Assert.Contains($"nowMs={nowMs}", reason);
        Assert.Contains($"lastDepthMs={depthUpdateMs}", reason);
        Assert.Contains("ageMs=2001", reason);
        Assert.Contains("thresholdMs=2000", reason);
        Assert.Contains("timeSource=UnixEpoch", reason);
    }

    [Fact]
    public void IsBookValid_JustFresh_OneLessThanThreshold_ReturnsValid()
    {
        // Arrange: depth updated 1999ms ago (just under 2000ms threshold)
        var book = CreateValidBook();
        var nowMs = 1000000000L;
        var depthUpdateMs = nowMs - 1999;
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, depthUpdateMs));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, depthUpdateMs));

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        Assert.True(isValid, $"Expected valid but got reason: {reason}");
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void IsBookValid_VeryStale_ReturnsInvalidWithDiagnostics()
    {
        // Arrange: depth updated 60 seconds ago
        var book = CreateValidBook();
        var nowMs = 1000000000L;
        var depthUpdateMs = nowMs - 60000;
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, depthUpdateMs));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, depthUpdateMs));

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        Assert.False(isValid);
        Assert.Contains("ageMs=60000", reason);
    }

    [Fact]
    public void IsBookValid_NegativeAge_ClockSkew_ReturnsValid()
    {
        // Arrange: depth timestamp is in the future (clock skew)
        var book = CreateValidBook();
        var nowMs = 1000000000L;
        var depthUpdateMs = nowMs + 1000; // 1 second in the future
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, depthUpdateMs));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, depthUpdateMs));

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        // Negative age should not trigger staleness (age = nowMs - lastDepthMs = -1000)
        Assert.True(isValid, $"Expected valid but got reason: {reason}");
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void IsBookValid_ZeroLastDepthUpdate_ReturnsInvalidStale()
    {
        // Arrange: valid book but with very old depth (LastDepthUpdateUtcMs = 0)
        var book = CreateValidBook();
        // Set depth with timestamp 0, then check with current time
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Bid, DepthOperation.Insert, 100m, 10m, 0, 0));
        book.ApplyDepthUpdate(new DepthUpdate("TEST", DepthSide.Ask, DepthOperation.Insert, 100.10m, 10m, 0, 0));
        var nowMs = 1000000000L;

        // Act
        var isValid = book.IsBookValid(out var reason, nowMs);

        // Assert
        // With nowMs=1000000000 and lastDepthMs=0, age will be huge and exceed threshold
        Assert.False(isValid);
        Assert.StartsWith("StaleDepthData", reason);
        Assert.Contains("lastDepthMs=0", reason);
    }

    private static OrderBookState CreateValidBook()
    {
        return new OrderBookState("TEST")
        {
            MaxDepthRows = 10
        };
    }
}
