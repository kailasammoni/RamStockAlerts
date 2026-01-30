namespace RamStockAlerts.Execution.Tests;

using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
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
    public void TrackSubmittedOrder_DuplicateOrderId_IgnoresSecond()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var firstIntent = Guid.NewGuid();
        var secondIntent = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, firstIntent, "AAPL", 10m, OrderSide.Buy);
        tracker.TrackSubmittedOrder(1, secondIntent, "MSFT", 5m, OrderSide.Sell);

        var field = typeof(OrderStateTracker).GetField("_orders", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var orders = field!.GetValue(tracker) as System.Collections.IDictionary;
        Assert.NotNull(orders);
        Assert.Equal(1, orders!.Count);
    }

    [Fact]
    public void ProcessOrderStatus_UpdatesStatus()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var intentId = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, intentId, "AAPL", 10m, OrderSide.Buy);

        var fired = false;
        int? capturedParent = null;
        decimal capturedPrice = 0m;
        DateTimeOffset capturedTime = default;
        var timestamp = DateTimeOffset.UtcNow;
        tracker.OnOrderFilled += (orderId, parentId, fillPrice, fillTime) =>
        {
            if (orderId == 1)
            {
                fired = true;
                capturedParent = parentId;
                capturedPrice = fillPrice;
                capturedTime = fillTime;
            }
        };

        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = BrokerOrderStatus.Filled,
            FilledQuantity = 10m,
            RemainingQuantity = 0m,
            AvgFillPrice = 100m,
            LastFillPrice = 100m,
            PermId = 1,
            ParentId = 42,
            TimestampUtc = timestamp
        });

        Assert.Equal(BrokerOrderStatus.Filled, tracker.GetOrderStatus(1));
        Assert.True(fired);
        Assert.Equal(42, capturedParent);
        Assert.Equal(100m, capturedPrice);
        Assert.Equal(timestamp, capturedTime);
    }

    [Fact]
    public void ProcessOrderStatus_Error_UpdatesStatus()
    {
        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance);
        var intentId = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, intentId, "AAPL", 10m, OrderSide.Buy);

        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = BrokerOrderStatus.Error,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(BrokerOrderStatus.Error, tracker.GetOrderStatus(1));
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
    public void ProcessCommissionReport_UpdatesRealizedPnlOnce()
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
            ExecutionTimeUtc = DateTimeOffset.UtcNow
        });

        tracker.ProcessCommissionReport("exec-1", commission: 1.2m, realizedPnl: -25m);
        tracker.ProcessCommissionReport("exec-1", commission: 1.2m, realizedPnl: -25m);

        Assert.Equal(-25m, tracker.GetRealizedPnlToday());
    }

    [Fact]
    public void ProcessOrderStatus_CancelledBracketLeg_DoesNotOverrideClosedState()
    {
        var ledger = new TestExecutionLedger();
        var entryIntent = new OrderIntent { IntentId = Guid.NewGuid(), Symbol = "AAPL" };
        var stopIntent = new OrderIntent { IntentId = Guid.NewGuid(), Symbol = "AAPL" };
        var tpIntent = new OrderIntent { IntentId = Guid.NewGuid(), Symbol = "AAPL" };

        ledger.RecordBracket(new BracketIntent
        {
            Entry = entryIntent,
            StopLoss = stopIntent,
            TakeProfit = tpIntent
        });
        ledger.UpdateBracketState(entryIntent.IntentId, BracketState.ClosedWin);

        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance, ledger);

        tracker.TrackSubmittedOrder(1, tpIntent.IntentId, "AAPL", 10m, OrderSide.Sell);
        tracker.TrackSubmittedOrder(2, stopIntent.IntentId, "AAPL", 10m, OrderSide.Sell);

        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 2,
            Status = BrokerOrderStatus.Cancelled,
            FilledQuantity = 0m,
            RemainingQuantity = 10m,
            AvgFillPrice = 0m,
            LastFillPrice = 0m,
            PermId = 1,
            ParentId = 0,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(BracketState.ClosedWin, ledger.GetBracketState(entryIntent.IntentId));
    }

    [Theory]
    [InlineData(BrokerOrderStatus.Cancelled)]
    [InlineData(BrokerOrderStatus.Inactive)]
    public void ProcessOrderStatus_EntryCancelled_StateIsPropagated(BrokerOrderStatus status)
    {
        var ledger = new TestExecutionLedger();
        var entryIntent = new OrderIntent { IntentId = Guid.NewGuid(), Symbol = "AAPL" };

        ledger.RecordBracket(new BracketIntent
        {
            Entry = entryIntent
        });
        ledger.UpdateBracketState(entryIntent.IntentId, BracketState.Pending);

        var tracker = new OrderStateTracker(NullLogger<OrderStateTracker>.Instance, ledger);

        tracker.TrackSubmittedOrder(1, entryIntent.IntentId, "AAPL", 10m, OrderSide.Buy);
        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = status,
            FilledQuantity = 0m,
            RemainingQuantity = 10m,
            AvgFillPrice = 0m,
            LastFillPrice = 0m,
            PermId = 1,
            ParentId = 0,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(BracketState.Cancelled, ledger.GetBracketState(entryIntent.IntentId));
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

    [Fact]
    public void CheckForStaleOrders_WarnsOnlyOnThresholdCrossing()
    {
        var logger = new CapturingLogger<OrderStateTracker>();
        var tracker = new OrderStateTracker(logger);
        var intentId = Guid.NewGuid();

        tracker.TrackSubmittedOrder(1, intentId, "AAPL", 10m, OrderSide.Buy);
        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = BrokerOrderStatus.Submitted,
            TimestampUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(70)
        });

        tracker.CheckForStaleOrders();
        Assert.Equal(1, logger.Entries.Count(e => e.Level == LogLevel.Warning));

        tracker.CheckForStaleOrders();
        Assert.Equal(1, logger.Entries.Count(e => e.Level == LogLevel.Warning));

        tracker.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = 1,
            Status = BrokerOrderStatus.Submitted,
            TimestampUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(130)
        });

        tracker.CheckForStaleOrders();
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));

        tracker.CheckForStaleOrders();
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));

        tracker.Dispose();
    }

    private sealed class TestExecutionLedger : IExecutionLedger
    {
        private readonly List<BracketIntent> _brackets = new();
        private readonly Dictionary<Guid, BracketState> _states = new();

        public void RecordIntent(OrderIntent intent)
        {
        }

        public void RecordBracket(BracketIntent intent)
        {
            _brackets.Add(intent);
        }

        public void RecordResult(Guid intentId, ExecutionResult result)
        {
        }

        public IReadOnlyList<OrderIntent> GetIntents() => Array.Empty<OrderIntent>();

        public IReadOnlyList<BracketIntent> GetBrackets() => _brackets.AsReadOnly();

        public IReadOnlyList<ExecutionResult> GetResults() => Array.Empty<ExecutionResult>();

        public int GetSubmittedIntentCountToday(DateTimeOffset now) => 0;

        public int GetSubmittedBracketCountToday(DateTimeOffset now) => 0;

        public int GetOpenBracketCount() => _states.Values.Count(state => state == BracketState.Open);

        public Guid? GetDecisionIdByOrderId(int orderId) => null;

        public void UpdateBracketState(Guid entryIntentId, BracketState newState)
        {
            _states[entryIntentId] = newState;
        }

        public BracketState GetBracketState(Guid entryIntentId) => _states[entryIntentId];
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        public List<Entry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
