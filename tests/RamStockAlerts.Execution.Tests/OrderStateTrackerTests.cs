namespace RamStockAlerts.Execution.Tests;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Services;
using Xunit;

public class OrderStateTrackerTests
{
    [Fact]
    public void TrackSubmittedOrder_TracksStatus()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var intentId = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, intentId, "AAPL", 10m, OrderSide.Buy);

        Assert.Equal(BrokerOrderStatus.Submitted, tracker.GetOrderStatus(1));
    }

    [Fact]
    public void ProcessOrderStatus_UpdatesStatus()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var intentId = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, intentId, "AAPL", 10m, OrderSide.Buy);

        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = BrokerOrderStatus.Filled,
            FilledQuantity = 10m,
            RemainingQuantity = 0m,
            AvgFillPrice = 100m,
            LastFillPrice = 100m,
            PermId = 1,
            ParentId = 0,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(BrokerOrderStatus.Filled, tracker.GetOrderStatus(1));
    }

    [Fact]
    public void ProcessFill_AccumulatesRealizedPnl()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);

        tracker.ProcessFill(new FillReport
        {
            OrderId = 1,
            Symbol = "AAPL",
            Side = "BOT",
            Quantity = 10m,
            Price = 100m,
            ExecId = "exec-1",
            ExecutionTimeUtc = DateTimeOffset.UtcNow,
            RealizedPnl = 50m
        });

        tracker.ProcessFill(new FillReport
        {
            OrderId = 2,
            Symbol = "AAPL",
            Side = "SLD",
            Quantity = 10m,
            Price = 105m,
            ExecId = "exec-2",
            ExecutionTimeUtc = DateTimeOffset.UtcNow,
            RealizedPnl = -20m
        });

        Assert.Equal(30m, tracker.GetRealizedPnlToday());
    }

    [Fact]
    public void GetRealizedPnlToday_DayRollover_Resets()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var type = tracker.GetType();

        var pnlField = type.GetField("_realizedPnlToday", BindingFlags.NonPublic | BindingFlags.Instance);
        var dateField = type.GetField("_currentTradeDate", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(pnlField);
        Assert.NotNull(dateField);

        pnlField!.SetValue(tracker, 100m);
        dateField!.SetValue(tracker, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));

        Assert.Equal(0m, tracker.GetRealizedPnlToday());
    }
}
