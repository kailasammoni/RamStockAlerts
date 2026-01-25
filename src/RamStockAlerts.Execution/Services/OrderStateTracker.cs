namespace RamStockAlerts.Execution.Services;

using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

public sealed class OrderStateTracker : IOrderStateTracker
{
    private readonly ILogger<OrderStateTracker> _logger;
    private readonly IExecutionLedger? _ledger;
    private readonly ConcurrentDictionary<int, TrackedOrder> _orders = new();
    private readonly ConcurrentDictionary<int, List<FillReport>> _fills = new();
    private readonly ConcurrentDictionary<Guid, List<int>> _intentOrders = new();

    private decimal _realizedPnlToday = 0m;
    private DateOnly _currentTradeDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly object _pnlLock = new();
    private long _fillCount;

    public OrderStateTracker(ILogger<OrderStateTracker> logger, IExecutionLedger? ledger = null)
    {
        _logger = logger;
        _ledger = ledger;
    }

    public void TrackSubmittedOrder(int orderId, Guid intentId, string symbol, decimal quantity, OrderSide side)
    {
        var tracked = new TrackedOrder
        {
            OrderId = orderId,
            IntentId = intentId,
            Symbol = symbol,
            Quantity = quantity,
            Side = side,
            Status = BrokerOrderStatus.Submitted,
            SubmittedUtc = DateTimeOffset.UtcNow
        };

        _orders[orderId] = tracked;
        _intentOrders.AddOrUpdate(
            intentId,
            _ => new List<int> { orderId },
            (_, list) =>
            {
                list.Add(orderId);
                return list;
            });

        _logger.LogInformation(
            "[OrderTracker] Tracking order {OrderId} for intent {IntentId} symbol={Symbol} qty={Qty} side={Side}",
            orderId,
            intentId,
            symbol,
            quantity,
            side);
    }

    public void ProcessOrderStatus(OrderStatusUpdate update)
    {
        ResetIfNewDay();

        if (!_orders.TryGetValue(update.OrderId, out var tracked))
        {
            _logger.LogDebug(
                "[OrderTracker] Status update for unknown order {OrderId}: {Status}",
                update.OrderId,
                update.Status);
            return;
        }

        var oldStatus = tracked.Status;
        tracked.Status = update.Status;
        tracked.FilledQuantity = update.FilledQuantity;
        tracked.AvgFillPrice = update.AvgFillPrice;
        tracked.LastUpdateUtc = update.TimestampUtc;

        _logger.LogInformation(
            "[OrderTracker] Order {OrderId} status: {OldStatus} → {NewStatus} filled={Filled}/{Total} avgPrice={AvgPrice}",
            update.OrderId,
            oldStatus,
            update.Status,
            update.FilledQuantity,
            tracked.Quantity,
            update.AvgFillPrice);

        if (update.Status == BrokerOrderStatus.Cancelled || update.Status == BrokerOrderStatus.Inactive)
        {
            UpdateBracketStateForIntent(tracked.IntentId, BracketState.Cancelled);
        }
        else if (update.Status == BrokerOrderStatus.Filled)
        {
            UpdateBracketStateForIntent(tracked.IntentId, null);
        }
    }

    public void ProcessFill(FillReport fill)
    {
        ResetIfNewDay();

        _fills.AddOrUpdate(
            fill.OrderId,
            _ => new List<FillReport> { fill },
            (_, list) =>
            {
                list.Add(fill);
                return list;
            });

        var fillCount = Interlocked.Increment(ref _fillCount);

        if (fill.RealizedPnl.HasValue)
        {
            lock (_pnlLock)
            {
                _realizedPnlToday += fill.RealizedPnl.Value;
            }

            _logger.LogInformation(
                "[OrderTracker] Fill {ExecId} order={OrderId} realized P&L=${Pnl} (daily total=${DailyPnl})",
                fill.ExecId,
                fill.OrderId,
                fill.RealizedPnl,
                _realizedPnlToday);
        }
        else
        {
            _logger.LogInformation(
                "[OrderTracker] Fill {ExecId} order={OrderId} qty={Qty} price={Price} fillsToday={FillCount}",
                fill.ExecId,
                fill.OrderId,
                fill.Quantity,
                fill.Price,
                fillCount);
        }

        if (_orders.TryGetValue(fill.OrderId, out var tracked))
        {
            UpdateBracketStateForIntent(tracked.IntentId, null);
        }
    }

    public BrokerOrderStatus GetOrderStatus(int orderId)
        => _orders.TryGetValue(orderId, out var t) ? t.Status : BrokerOrderStatus.Unknown;

    public IReadOnlyList<FillReport> GetFillsForOrder(int orderId)
        => _fills.TryGetValue(orderId, out var list) ? list.ToList() : Array.Empty<FillReport>();

    public IReadOnlyList<FillReport> GetFillsForIntent(Guid intentId)
    {
        if (!_intentOrders.TryGetValue(intentId, out var orderIds))
            return Array.Empty<FillReport>();

        return orderIds
            .SelectMany(id => GetFillsForOrder(id))
            .ToList();
    }

    public decimal GetRealizedPnlToday()
    {
        ResetIfNewDay();
        lock (_pnlLock)
        {
            return _realizedPnlToday;
        }
    }

    public int GetOpenBracketCount()
    {
        if (_ledger is not null)
        {
            return _ledger.GetOpenBracketCount();
        }

        return _orders.Values
            .Count(o => o.Status == BrokerOrderStatus.Filled && o.Side != OrderSide.Sell);
    }

    private void ResetIfNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _currentTradeDate)
        {
            lock (_pnlLock)
            {
                _logger.LogInformation(
                    "[OrderTracker] New trading day. Resetting daily P&L from ${Old} to $0",
                    _realizedPnlToday);
                _realizedPnlToday = 0m;
                _currentTradeDate = today;
            }
        }
    }

    private void UpdateBracketStateForIntent(Guid intentId, BracketState? fallbackState)
    {
        if (_ledger is null)
        {
            return;
        }

        var brackets = _ledger.GetBrackets();
        foreach (var bracket in brackets)
        {
            if (bracket.Entry.IntentId == intentId)
            {
                _ledger.UpdateBracketState(intentId, BracketState.Open);
                return;
            }

            if (bracket.StopLoss?.IntentId == intentId)
            {
                _ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedLoss);
                return;
            }

            if (bracket.TakeProfit?.IntentId == intentId)
            {
                _ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedWin);
                return;
            }
        }

        if (fallbackState.HasValue)
        {
            _ledger.UpdateBracketState(intentId, fallbackState.Value);
        }
    }

    private sealed class TrackedOrder
    {
        public int OrderId { get; init; }
        public Guid IntentId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public OrderSide Side { get; init; }
        public BrokerOrderStatus Status { get; set; }
        public decimal FilledQuantity { get; set; }
        public decimal AvgFillPrice { get; set; }
        public DateTimeOffset SubmittedUtc { get; init; }
        public DateTimeOffset LastUpdateUtc { get; set; }
    }
}
