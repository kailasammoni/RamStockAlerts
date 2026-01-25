namespace RamStockAlerts.Execution.Services;

using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

public sealed class OrderStateTracker : IOrderStateTracker, IDisposable
{
    private readonly ILogger<OrderStateTracker> _logger;
    private readonly IExecutionLedger? _ledger;
    private readonly IPostSignalMonitor? _postSignalMonitor;
    private readonly ConcurrentDictionary<int, TrackedOrder> _orders = new();
    private readonly ConcurrentDictionary<int, List<FillReport>> _fills = new();
    private readonly ConcurrentDictionary<string, FillReport> _fillsByExecId = new();
    private readonly ConcurrentDictionary<string, decimal> _realizedPnlByExecId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, List<int>> _intentOrders = new();

    private readonly object _bracketStateLock = new();
    private readonly TimeSpan _orderStatusTimeout = TimeSpan.FromSeconds(30);
    private readonly Timer _staleOrderTimer;

    private decimal _realizedPnlToday = 0m;
    private DateOnly _currentTradeDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly object _pnlLock = new();
    private long _fillCount;

    public OrderStateTracker(
        ILogger<OrderStateTracker> logger,
        IExecutionLedger? ledger = null,
        IPostSignalMonitor? postSignalMonitor = null)
    {
        _logger = logger;
        _ledger = ledger;
        _postSignalMonitor = postSignalMonitor;
        _staleOrderTimer = new Timer(
            _ => CheckForStaleOrders(),
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15));
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
            SubmittedUtc = DateTimeOffset.UtcNow,
            LastUpdateUtc = DateTimeOffset.UtcNow
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
            UpdateBracketStateForIntent(tracked.IntentId, BracketState.Cancelled, update.Status);
        }
        else if (update.Status == BrokerOrderStatus.Filled)
        {
            UpdateBracketStateForIntent(tracked.IntentId, null, update.Status);
        }
    }

    public void ProcessFill(FillReport fill)
    {
        ResetIfNewDay();

        if (!string.IsNullOrWhiteSpace(fill.ExecId))
        {
            _fillsByExecId[fill.ExecId] = fill;
        }

        _fills.AddOrUpdate(
            fill.OrderId,
            _ => new List<FillReport> { fill },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(fill);
                }
                return list;
            });

        var fillCount = Interlocked.Increment(ref _fillCount);

        if (fill.RealizedPnl.HasValue)
        {
            if (string.IsNullOrWhiteSpace(fill.ExecId))
            {
                lock (_pnlLock)
                {
                    _realizedPnlToday += fill.RealizedPnl.Value;
                }

                LogDailyPnl(fill.ExecId, fill.OrderId, fill.RealizedPnl.Value);
            }
            else if (TryRecordRealizedPnl(fill.ExecId, fill.RealizedPnl.Value))
            {
                LogDailyPnl(fill.ExecId, fill.OrderId, fill.RealizedPnl.Value);
            }
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
            tracked.LastUpdateUtc = fill.ExecutionTimeUtc;
            UpdateBracketStateForIntent(tracked.IntentId, null, BrokerOrderStatus.Filled);
        }
    }

    public void ProcessCommissionReport(string execId, decimal? commission, decimal? realizedPnl)
    {
        ResetIfNewDay();

        if (string.IsNullOrWhiteSpace(execId))
        {
            _logger.LogWarning("[OrderTracker] Commission report missing execId. Commission={Commission} Pnl={Pnl}", commission, realizedPnl);
            return;
        }

        if (realizedPnl.HasValue && TryRecordRealizedPnl(execId, realizedPnl.Value))
        {
            var orderId = _fillsByExecId.TryGetValue(execId, out var fill) ? fill.OrderId : 0;
            LogDailyPnl(execId, orderId, realizedPnl.Value);
        }

        if (_fillsByExecId.TryGetValue(execId, out var existingFill))
        {
            var updatedFill = new FillReport
            {
                OrderId = existingFill.OrderId,
                Symbol = existingFill.Symbol,
                Side = existingFill.Side,
                Quantity = existingFill.Quantity,
                Price = existingFill.Price,
                ExecId = existingFill.ExecId,
                ExecutionTimeUtc = existingFill.ExecutionTimeUtc,
                Commission = commission ?? existingFill.Commission,
                RealizedPnl = realizedPnl ?? existingFill.RealizedPnl
            };

            _fillsByExecId[execId] = updatedFill;

            _fills.AddOrUpdate(
                existingFill.OrderId,
                _ => new List<FillReport> { updatedFill },
                (_, list) =>
                {
                    lock (list)
                    {
                        var index = list.FindIndex(f => f.ExecId == execId);
                        if (index >= 0)
                        {
                            list[index] = updatedFill;
                        }
                        else
                        {
                            list.Add(updatedFill);
                        }
                    }
                    return list;
                });
        }

        _logger.LogInformation(
            "[OrderTracker] Commission report execId={ExecId} commission={Commission} realizedPnl={Pnl}",
            execId,
            commission,
            realizedPnl);
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

    private void UpdateBracketStateForIntent(Guid intentId, BracketState? fallbackState, BrokerOrderStatus status)
    {
        if (_ledger is null)
        {
            return;
        }

        string? entrySymbolToNotify = null;
        bool entryOpened = false;
        var handled = false;

        lock (_bracketStateLock)
        {
            var brackets = _ledger.GetBrackets();
            foreach (var bracket in brackets)
            {
                if (bracket.Entry.IntentId == intentId)
                {
                    handled = true;
                    var targetState = status == BrokerOrderStatus.Filled ? BracketState.Open : fallbackState;
                    if (targetState.HasValue)
                    {
                        _ledger.UpdateBracketState(intentId, targetState.Value);
                        entryOpened = targetState.Value == BracketState.Open;
                        if (entryOpened)
                        {
                            entrySymbolToNotify = bracket.Entry.Symbol;
                        }
                    }
                    break;
                }

                if (bracket.StopLoss?.IntentId == intentId)
                {
                    handled = true;
                    if (status == BrokerOrderStatus.Filled)
                    {
                        _ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedLoss);
                    }
                    break;
                }

                if (bracket.TakeProfit?.IntentId == intentId)
                {
                    handled = true;
                    if (status == BrokerOrderStatus.Filled)
                    {
                        _ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedWin);
                    }
                    break;
                }
            }

            if (!handled && fallbackState.HasValue)
            {
                _ledger.UpdateBracketState(intentId, fallbackState.Value);
            }
        }

        if (entryOpened && !string.IsNullOrWhiteSpace(entrySymbolToNotify))
        {
            _postSignalMonitor?.OnEntryFilled(entrySymbolToNotify);
        }
    }

    private bool TryRecordRealizedPnl(string execId, decimal realizedPnl)
    {
        if (string.IsNullOrWhiteSpace(execId))
        {
            return false;
        }

        if (!_realizedPnlByExecId.TryAdd(execId, realizedPnl))
        {
            return false;
        }

        lock (_pnlLock)
        {
            _realizedPnlToday += realizedPnl;
        }

        return true;
    }

    private void LogDailyPnl(string execId, int orderId, decimal realizedPnl)
    {
        _logger.LogInformation(
            "[OrderTracker] Fill {ExecId} order={OrderId} realized P&L=${Pnl} (daily total=${DailyPnl})",
            execId,
            orderId,
            realizedPnl,
            _realizedPnlToday);
    }

    public IReadOnlyList<int> GetStaleOrders()
    {
        var now = DateTimeOffset.UtcNow;
        return _orders.Values
            .Where(o => o.Status == BrokerOrderStatus.Submitted
                        && o.LastUpdateUtc != default
                        && (now - o.LastUpdateUtc) > _orderStatusTimeout)
            .Select(o => o.OrderId)
            .ToList();
    }

    public void CheckForStaleOrders()
    {
        var stale = GetStaleOrders();
        foreach (var orderId in stale)
        {
            if (_orders.TryGetValue(orderId, out var order))
            {
                _logger.LogWarning(
                    "[OrderTracker] STALE ORDER ALERT: Order {OrderId} ({Symbol}) no status update for {Elapsed}s",
                    orderId,
                    order.Symbol,
                    (DateTimeOffset.UtcNow - order.LastUpdateUtc).TotalSeconds);
            }
        }
    }

    public void Dispose()
    {
        _staleOrderTimer.Dispose();
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
