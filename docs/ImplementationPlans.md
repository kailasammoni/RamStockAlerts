# Implementation Plans for RamStockAlerts

This document provides detailed implementation plans for 6 critical features. Each plan is self-contained and can be executed independently by any AI agent.

---

## Table of Contents

### Core Features
1. [IBKR Order Status Callbacks](#1-ibkr-order-status-callbacks-l-1-2d)
2. [Fix P&L Calculation](#2-fix-pl-calculation-s-1h)
3. [Daily Loss Limit Enforcement](#3-daily-loss-limit-enforcement-m-2-3h)
4. [Fix Ledger Counting](#4-fix-ledger-counting-m-2h)
5. [Hard Rejection Gates](#5-hard-rejection-gates-m-2h)
6. [Post-Signal Monitor](#6-post-signal-monitor-l-1-2d)

### Enhancements (Bookmarked)
7. [Additional Enhancements](#7-additional-enhancements-bookmarked)
   - 7.1 [Pre-Fill Monitoring](#71-pre-fill-monitoring-enhancement-to-6) - Monitor spread/tape before entry fill
   - 7.2 [Broker Failure Contingencies](#72-broker-failure-contingencies-enhancement-to-1) - Timeout alerts for stale orders
   - 7.3 [Throttle Post-Signal Monitoring](#73-throttle-post-signal-monitoring-load-enhancement-to-6) - Prevent CPU starvation
   - 7.4 [Soft Kill-Switch Toggle](#74-soft-kill-switch-toggle-new-feature) - Monitor-only mode
   - 7.5 [Tag Trades by Quality Bands](#75-tag-trades-by-quality-bands-future-analytics) - Cohort analysis

---

## 1. IBKR Order Status Callbacks (L, 1-2d)

### Problem Statement
The `IbkrBrokerClient.Wrapper` class has empty implementations for `orderStatus`, `execDetails`, and `commissionAndFeesReport`. Without processing these callbacks, the system cannot track:
- Order fills (partial/complete)
- Fill prices
- Order cancellations
- Position state changes

This breaks P&L tracking, position counting, and daily loss enforcement.

### Current State
**File**: `src/RamStockAlerts.Execution/Services/IbkrBrokerClient.cs` (lines 408-411)
```csharp
public override void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
public override void execDetails(int reqId, Contract contract, IBApi.Execution execution) { }
public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport) { }
```

### Implementation Plan

#### Step 1: Create Order Tracking Models
**Create file**: `src/RamStockAlerts.Execution/Contracts/OrderStatusUpdate.cs`
```csharp
namespace RamStockAlerts.Execution.Contracts;

public enum BrokerOrderStatus
{
    Unknown,
    PendingSubmit,
    PreSubmitted,
    Submitted,
    Filled,
    PartiallyFilled,
    Cancelled,
    Inactive,
    Error
}

public sealed class OrderStatusUpdate
{
    public int OrderId { get; init; }
    public BrokerOrderStatus Status { get; init; }
    public decimal FilledQuantity { get; init; }
    public decimal RemainingQuantity { get; init; }
    public decimal AvgFillPrice { get; init; }
    public decimal LastFillPrice { get; init; }
    public long PermId { get; init; }
    public int ParentId { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
}

public sealed class FillReport
{
    public int OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;  // "BOT" or "SLD"
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public string ExecId { get; init; } = string.Empty;
    public DateTimeOffset ExecutionTimeUtc { get; init; }
    public decimal? Commission { get; init; }
    public decimal? RealizedPnl { get; init; }
}
```

#### Step 2: Create Order State Tracker Interface
**Create file**: `src/RamStockAlerts.Execution/Interfaces/IOrderStateTracker.cs`
```csharp
namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

public interface IOrderStateTracker
{
    void TrackSubmittedOrder(int orderId, Guid intentId, string symbol, decimal quantity, OrderSide side);
    void ProcessOrderStatus(OrderStatusUpdate update);
    void ProcessFill(FillReport fill);
    
    BrokerOrderStatus GetOrderStatus(int orderId);
    IReadOnlyList<FillReport> GetFillsForOrder(int orderId);
    IReadOnlyList<FillReport> GetFillsForIntent(Guid intentId);
    
    decimal GetRealizedPnlToday();
    int GetOpenBracketCount();
}
```

#### Step 3: Implement Order State Tracker
**Create file**: `src/RamStockAlerts.Execution/Services/OrderStateTracker.cs`
```csharp
namespace RamStockAlerts.Execution.Services;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

public sealed class OrderStateTracker : IOrderStateTracker
{
    private readonly ILogger<OrderStateTracker> _logger;
    private readonly ConcurrentDictionary<int, TrackedOrder> _orders = new();
    private readonly ConcurrentDictionary<int, List<FillReport>> _fills = new();
    private readonly ConcurrentDictionary<Guid, List<int>> _intentOrders = new();
    
    // Track daily realized P&L
    private decimal _realizedPnlToday = 0m;
    private DateOnly _currentTradeDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private readonly object _pnlLock = new();

    public OrderStateTracker(ILogger<OrderStateTracker> logger)
    {
        _logger = logger;
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
        _intentOrders.AddOrUpdate(intentId, 
            _ => new List<int> { orderId },
            (_, list) => { list.Add(orderId); return list; });
            
        _logger.LogInformation("[OrderTracker] Tracking order {OrderId} for intent {IntentId} symbol={Symbol} qty={Qty} side={Side}",
            orderId, intentId, symbol, quantity, side);
    }

    public void ProcessOrderStatus(OrderStatusUpdate update)
    {
        ResetIfNewDay();
        
        if (!_orders.TryGetValue(update.OrderId, out var tracked))
        {
            _logger.LogDebug("[OrderTracker] Status update for unknown order {OrderId}: {Status}", 
                update.OrderId, update.Status);
            return;
        }
        
        var oldStatus = tracked.Status;
        tracked.Status = update.Status;
        tracked.FilledQuantity = update.FilledQuantity;
        tracked.AvgFillPrice = update.AvgFillPrice;
        tracked.LastUpdateUtc = update.TimestampUtc;
        
        _logger.LogInformation("[OrderTracker] Order {OrderId} status: {OldStatus} → {NewStatus} filled={Filled}/{Total} avgPrice={AvgPrice}",
            update.OrderId, oldStatus, update.Status, update.FilledQuantity, tracked.Quantity, update.AvgFillPrice);
    }

    public void ProcessFill(FillReport fill)
    {
        ResetIfNewDay();
        
        _fills.AddOrUpdate(fill.OrderId,
            _ => new List<FillReport> { fill },
            (_, list) => { list.Add(fill); return list; });
        
        if (fill.RealizedPnl.HasValue)
        {
            lock (_pnlLock)
            {
                _realizedPnlToday += fill.RealizedPnl.Value;
            }
            
            _logger.LogInformation("[OrderTracker] Fill {ExecId} order={OrderId} realized P&L=${Pnl} (daily total=${DailyPnl})",
                fill.ExecId, fill.OrderId, fill.RealizedPnl, _realizedPnlToday);
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
        // Count brackets where entry is filled but neither stop nor TP is filled/cancelled
        // Simplified: count entries with status Filled where related stops are still Submitted
        return _orders.Values
            .Where(o => o.Status == BrokerOrderStatus.Filled && o.Side != OrderSide.Sell)
            .Count();
    }

    private void ResetIfNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _currentTradeDate)
        {
            lock (_pnlLock)
            {
                _logger.LogInformation("[OrderTracker] New trading day. Resetting daily P&L from ${Old} to $0", _realizedPnlToday);
                _realizedPnlToday = 0m;
                _currentTradeDate = today;
            }
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
```

#### Step 4: Update IbkrBrokerClient Wrapper
**Modify file**: `src/RamStockAlerts.Execution/Services/IbkrBrokerClient.cs`

Replace the `Wrapper` class (starting at line 380):
```csharp
private sealed class Wrapper : DefaultEWrapper
{
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<int> _nextValidIdTcs;
    private readonly IOrderStateTracker? _orderTracker;

    public Wrapper(ILogger logger, TaskCompletionSource<int> nextValidIdTcs, IOrderStateTracker? orderTracker = null)
    {
        _logger = logger;
        _nextValidIdTcs = nextValidIdTcs;
        _orderTracker = orderTracker;
    }

    public override void nextValidId(int orderId) => _nextValidIdTcs.TrySetResult(orderId);

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        if (errorCode is 2104 or 2106 or 2158 or 2107)
            return;
        _logger.LogWarning("[IBKR] error id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
    }

    public override void error(string str) => _logger.LogWarning("[IBKR] error: {Message}", str);
    public override void error(Exception e) => _logger.LogWarning(e, "[IBKR] exception");
    public override void connectionClosed() => _logger.LogWarning("[IBKR] Connection closed");

    public override void orderStatus(int orderId, string status, decimal filled, decimal remaining, 
        double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        var brokerStatus = MapStatus(status);
        
        _logger.LogDebug("[IBKR] orderStatus id={OrderId} status={Status} filled={Filled} remaining={Remaining} avgPrice={AvgPrice}",
            orderId, status, filled, remaining, avgFillPrice);
        
        _orderTracker?.ProcessOrderStatus(new OrderStatusUpdate
        {
            OrderId = orderId,
            Status = brokerStatus,
            FilledQuantity = filled,
            RemainingQuantity = remaining,
            AvgFillPrice = (decimal)avgFillPrice,
            LastFillPrice = (decimal)lastFillPrice,
            PermId = permId,
            ParentId = parentId,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    public override void execDetails(int reqId, Contract contract, IBApi.Execution execution)
    {
        _logger.LogDebug("[IBKR] execDetails reqId={ReqId} symbol={Symbol} side={Side} qty={Qty} price={Price} execId={ExecId}",
            reqId, contract.Symbol, execution.Side, execution.Shares, execution.Price, execution.ExecId);
        
        _orderTracker?.ProcessFill(new FillReport
        {
            OrderId = execution.OrderId,
            Symbol = contract.Symbol,
            Side = execution.Side,
            Quantity = execution.Shares,
            Price = (decimal)execution.Price,
            ExecId = execution.ExecId,
            ExecutionTimeUtc = DateTimeOffset.UtcNow
        });
    }

    public override void commissionAndFeesReport(CommissionAndFeesReport report)
    {
        _logger.LogDebug("[IBKR] commission execId={ExecId} commission={Commission} realizedPnl={Pnl}",
            report.ExecId, report.Commission, report.RealizedPNL);
        
        // Note: This arrives after execDetails - need to correlate via ExecId
        // For now, log only; correlation can be added later
    }

    private static BrokerOrderStatus MapStatus(string status) => status.ToUpperInvariant() switch
    {
        "PENDINGSUBMIT" => BrokerOrderStatus.PendingSubmit,
        "PRESUBMITTED" => BrokerOrderStatus.PreSubmitted,
        "SUBMITTED" => BrokerOrderStatus.Submitted,
        "FILLED" => BrokerOrderStatus.Filled,
        "CANCELLED" => BrokerOrderStatus.Cancelled,
        "INACTIVE" => BrokerOrderStatus.Inactive,
        _ when status.Contains("PARTIAL", StringComparison.OrdinalIgnoreCase) => BrokerOrderStatus.PartiallyFilled,
        _ => BrokerOrderStatus.Unknown
    };
}
```

#### Step 5: Wire OrderStateTracker to IbkrBrokerClient
**Modify file**: `src/RamStockAlerts.Execution/Services/IbkrBrokerClient.cs`

Add field and update constructor:
```csharp
private readonly IOrderStateTracker? _orderTracker;

public IbkrBrokerClient(ILogger<IbkrBrokerClient> logger, IConfiguration configuration, IOrderStateTracker? orderTracker = null)
{
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    _orderTracker = orderTracker;
}
```

Update `EnsureConnectedAsync` to pass tracker:
```csharp
_wrapper = new Wrapper(_logger, _nextValidIdTcs, _orderTracker);
```

#### Step 6: Update PlaceAsync/PlaceBracketAsync to track orders
After `_socket!.placeOrder(orderId, contract, order);` add:
```csharp
_orderTracker?.TrackSubmittedOrder(orderId, intent.IntentId, intent.Symbol ?? "", (decimal)order.TotalQuantity, intent.Side);
```

#### Step 7: Register Services in DI
**Modify file**: `src/RamStockAlerts/Program.cs` (or wherever DI is configured)
```csharp
services.AddSingleton<IOrderStateTracker, OrderStateTracker>();
```

#### Step 8: Add Tests
**Create file**: `tests/RamStockAlerts.Execution.Tests/OrderStateTrackerTests.cs`
- Test `TrackSubmittedOrder` correctly tracks
- Test `ProcessOrderStatus` updates status correctly
- Test `ProcessFill` accumulates realized P&L
- Test `GetRealizedPnlToday` returns correct value
- Test day rollover resets P&L

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~OrderStateTracker"
```

### Telemetry Updates
- Log all order status transitions at INFO level
- Log fills with P&L at INFO level
- Add metrics counter for fills processed

---

## 2. Fix P&L Calculation (S, 1h)

### Problem Statement
**File**: `src/RamStockAlerts/Services/TradeOutcomeLabeler.cs` (line 100)
```csharp
outcome.PnlUsd = rawPnl; // TODO: Multiply by shares when available
```

Current issues:
1. P&L is price difference only, not multiplied by share count
2. R-multiple sign may be incorrect for shorts (comment on line 70-72 says "Negative means trade is profitable" which is backward)

### Current State
- `rawPnl = exitPrice - entryPrice` (correct for longs, inverted for shorts)
- `RiskMultiple = moveRange / riskRange` where `moveRange = exit - entry` (needs sign handling)
- No share count available from `TradeJournalEntry`

### Implementation Plan

#### Step 1: Add ShareCount to Blueprint
**Modify file**: `src/RamStockAlerts/Models/TradeJournalEntry.cs`

Add to `BlueprintPlan` class:
```csharp
public class BlueprintPlan
{
    public decimal? Entry { get; set; }
    public decimal? Stop { get; set; }
    public decimal? Target { get; set; }
    public int? ShareCount { get; set; }  // NEW: Number of shares for P&L calculation
}
```

#### Step 2: Fix P&L Calculation
**Modify file**: `src/RamStockAlerts/Services/TradeOutcomeLabeler.cs`

Replace lines 96-115:
```csharp
// Calculate P&L, risk multiple, and win flag
if (exitPrice.HasValue && outcome.EntryPrice.HasValue)
{
    var isLong = direction?.Equals("Long", StringComparison.OrdinalIgnoreCase) == true;
    var priceMove = exitPrice.Value - outcome.EntryPrice.Value;
    
    // For shorts, profit is when price goes DOWN (entry - exit), so invert
    var rawPnl = isLong ? priceMove : -priceMove;
    
    // Multiply by share count if available
    var shareCount = journalEntry.Blueprint?.ShareCount ?? 1;
    outcome.PnlUsd = rawPnl * shareCount;

    // Calculate risk multiple: positive = profitable, negative = loss
    if (outcome.StopPrice.HasValue && outcome.EntryPrice.Value != outcome.StopPrice.Value)
    {
        var riskPerShare = Math.Abs(outcome.EntryPrice.Value - outcome.StopPrice.Value);
        if (riskPerShare > 0)
        {
            // R-multiple: profit / risk (positive = win, negative = loss)
            outcome.RiskMultiple = rawPnl / riskPerShare;
        }
    }

    // Determine win
    outcome.IsWin = rawPnl > 0;
}
```

#### Step 3: Fix RiskMultiple Documentation
**Modify file**: `src/RamStockAlerts/Models/TradeOutcome.cs`

Update comment on line 69-72:
```csharp
/// <summary>
/// Risk multiple: (profit) / (risk per share).
/// Positive means profitable; negative means loss.
/// Example: +2.0 = made 2R; -1.0 = lost 1R (hit stop)
/// </summary>
public decimal? RiskMultiple { get; set; }
```

#### Step 4: Update Tests
**Modify file**: `tests/RamStockAlerts.Tests/OutcomeLabelingTests.cs`

Add test for short trade P&L:
```csharp
[Fact]
public void LabelOutcome_ShortWin_PositiveRMultiple()
{
    // Entry=100, Stop=105, Target=90, Exit=92 (profit for short)
    var entry = CreateJournalEntry("Short", 100m, 105m, 90m);
    entry.Blueprint!.ShareCount = 10;
    
    var outcome = _labeler.LabelOutcomeAsync(entry, 92m, DateTimeOffset.UtcNow).Result;
    
    Assert.True(outcome.IsWin);
    Assert.Equal(80m, outcome.PnlUsd);  // (100-92) * 10 = 80
    Assert.True(outcome.RiskMultiple > 0);  // Profit = positive R
}

[Fact]
public void LabelOutcome_LongWin_PositiveRMultiple()
{
    var entry = CreateJournalEntry("Long", 100m, 95m, 110m);
    entry.Blueprint!.ShareCount = 10;
    
    var outcome = _labeler.LabelOutcomeAsync(entry, 108m, DateTimeOffset.UtcNow).Result;
    
    Assert.True(outcome.IsWin);
    Assert.Equal(80m, outcome.PnlUsd);  // (108-100) * 10 = 80
    Assert.True(outcome.RiskMultiple > 0);  // Profit = positive R
}
```

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~OutcomeLabeling"
```

Expected log output:
```
Labeled outcome for {DecisionId}: AAPL Long @ 100 → 108 | HitTarget | R=1.6 | PnL=$80
```

---

## 3. Daily Loss Limit Enforcement (M, 2-3h)

### Problem Statement
`ExecutionOptions.MaxLossPerDayUsd` exists (default $200) but is never enforced. The `RiskManagerV0` has a placeholder comment but no implementation.

### Current State
**File**: `src/RamStockAlerts.Execution/Contracts/ExecutionOptions.cs` (lines 43-47)
```csharp
/// <summary>
/// Maximum loss per calendar day in USD (default: 200).
/// </summary>
public decimal MaxLossPerDayUsd { get; set; } = 200m;
```

**File**: `src/RamStockAlerts.Execution/Risk/RiskManagerV0.cs` (lines 127-130)
```csharp
// 5. LIVE MODE SAFETY CHECKS
if (_options.Live)
{
    // Future: add account equity checks, max notional % validation, etc.
}
```

### Implementation Plan

#### Step 1: Add IOrderStateTracker Dependency to RiskManager
**Modify file**: `src/RamStockAlerts.Execution/Risk/RiskManagerV0.cs`

Update constructor:
```csharp
private readonly IOrderStateTracker? _orderTracker;

public RiskManagerV0(
    ExecutionOptions options,
    IOrderStateTracker? orderTracker = null,
    decimal maxNotionalUsd = 2000m,
    decimal maxShares = 500m)
{
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _orderTracker = orderTracker;
    _maxNotionalUsd = maxNotionalUsd;
    _maxShares = maxShares;
}
```

#### Step 2: Add Daily Loss Check
**Modify file**: `src/RamStockAlerts.Execution/Risk/RiskManagerV0.cs`

Add before the LIVE MODE SAFETY CHECKS section (after ledger checks, around line 124):
```csharp
// 5. CHECK DAILY LOSS LIMIT (if order tracker available)
if (_orderTracker is not null)
{
    var realizedPnlToday = _orderTracker.GetRealizedPnlToday();
    if (realizedPnlToday < 0 && Math.Abs(realizedPnlToday) >= _options.MaxLossPerDayUsd)
    {
        return RiskDecision.Reject(
            $"Daily loss limit (${_options.MaxLossPerDayUsd}) reached. Current P&L: ${realizedPnlToday:F2}",
            new List<string> { "DailyLossLimit" });
    }
    
    // Also check if placing this order would breach the limit (estimated max loss)
    var estimatedMaxLoss = EstimateMaxLoss(intent);
    if (realizedPnlToday - estimatedMaxLoss < -_options.MaxLossPerDayUsd)
    {
        return RiskDecision.Reject(
            $"Order would breach daily loss limit. Current P&L: ${realizedPnlToday:F2}, Est. max loss: ${estimatedMaxLoss:F2}",
            new List<string> { "DailyLossLimitPreventive" });
    }
}
```

#### Step 3: Add EstimateMaxLoss Helper
**Add to RiskManagerV0.cs**:
```csharp
private static decimal EstimateMaxLoss(OrderIntent intent)
{
    // For a market/limit order, max loss is unknown without stop
    // Return 0 for non-stop orders (bracket validation handles stops separately)
    if (intent.StopPrice is null || intent.LimitPrice is null)
        return 0m;
    
    var qty = intent.Quantity ?? 
        (intent.NotionalUsd.HasValue && intent.LimitPrice > 0 
            ? Math.Floor(intent.NotionalUsd.Value / intent.LimitPrice.Value) 
            : 0m);
            
    var riskPerShare = Math.Abs(intent.LimitPrice.Value - intent.StopPrice.Value);
    return qty * riskPerShare;
}
```

#### Step 4: Add Same Check to Bracket Validation
In `Validate(BracketIntent intent, ...)` add after entry validation (around line 156):
```csharp
// Check daily loss limit for brackets (uses entry + stop to estimate max loss)
if (_orderTracker is not null && intent.Entry is not null && intent.StopLoss is not null)
{
    var realizedPnlToday = _orderTracker.GetRealizedPnlToday();
    var estimatedMaxLoss = EstimateBracketMaxLoss(intent);
    
    if (realizedPnlToday - estimatedMaxLoss < -_options.MaxLossPerDayUsd)
    {
        return RiskDecision.Reject(
            $"Bracket would breach daily loss limit. Current P&L: ${realizedPnlToday:F2}, Est. max loss: ${estimatedMaxLoss:F2}",
            new List<string> { "DailyLossLimitPreventive" });
    }
}
```

Add helper:
```csharp
private static decimal EstimateBracketMaxLoss(BracketIntent intent)
{
    var entry = intent.Entry;
    var stop = intent.StopLoss;
    
    if (entry?.LimitPrice is null || stop?.StopPrice is null)
        return 0m;
    
    var qty = entry.Quantity ?? 
        (entry.NotionalUsd.HasValue && entry.LimitPrice > 0 
            ? Math.Floor(entry.NotionalUsd.Value / entry.LimitPrice.Value) 
            : 0m);
            
    var riskPerShare = Math.Abs(entry.LimitPrice.Value - stop.StopPrice.Value);
    return qty * riskPerShare;
}
```

#### Step 5: Add Tests
**Create/Modify file**: `tests/RamStockAlerts.Execution.Tests/RiskManagerV0Tests.cs`

Add tests:
```csharp
[Fact]
public void Validate_DailyLossLimitReached_Rejects()
{
    var tracker = new FakeOrderStateTracker { RealizedPnlToday = -200m };
    var options = new ExecutionOptions { MaxLossPerDayUsd = 200m };
    var rm = new RiskManagerV0(options, tracker);
    
    var intent = CreateValidIntent();
    var result = rm.Validate(intent);
    
    Assert.False(result.Allowed);
    Assert.Contains("DailyLossLimit", result.Tags);
}

[Fact]
public void Validate_OrderWouldBreachLimit_RejectsPreventively()
{
    var tracker = new FakeOrderStateTracker { RealizedPnlToday = -150m };
    var options = new ExecutionOptions { MaxLossPerDayUsd = 200m };
    var rm = new RiskManagerV0(options, tracker);
    
    // Order with $100 max loss would push us to -$250
    var intent = CreateIntentWithRisk(100m);
    var result = rm.Validate(intent);
    
    Assert.False(result.Allowed);
    Assert.Contains("DailyLossLimitPreventive", result.Tags);
}
```

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~RiskManagerV0"
```

Expected log when limit hit:
```
[WARN] Risk rejected: Daily loss limit ($200) reached. Current P&L: $-215.50
```

---

## 4. Fix Ledger Counting (M, 2h)

### Problem Statement
1. `InMemoryExecutionLedger` counts ALL recorded intents, including rejected ones, for daily limits
2. `CountOpenPositions` treats all brackets today as "open" (no status tracking)
3. No tracking of bracket lifecycle (submitted → filled → closed)

### Current State
**File**: `src/RamStockAlerts.Execution/Risk/RiskManagerV0.cs` (lines 211-250)
```csharp
private int CountOrdersToday(IExecutionLedger ledger, DateTimeOffset now)
{
    // Counts ALL intents, including rejected
    return ledger.GetIntents()
        .Count(o => o.CreatedUtc >= today_start && o.CreatedUtc < today_end);
}

private int CountOpenPositions(IExecutionLedger ledger, DateTimeOffset now)
{
    // Simplified: count active brackets (not yet closed/cancelled)
    // F6 will track order status properly; for now, assume all brackets are open
    return ledger.GetBrackets()
        .Count(b => b.Entry.CreatedUtc >= today_start && b.Entry.CreatedUtc < today_end);
}
```

### Implementation Plan

#### Step 1: Add Bracket State Enum
**Modify file**: `src/RamStockAlerts.Execution/Contracts/Enums.cs`

Add:
```csharp
public enum BracketState
{
    Pending,      // Submitted, awaiting entry fill
    Open,         // Entry filled, stop/TP active
    ClosedWin,    // TP hit
    ClosedLoss,   // Stop hit
    Cancelled,    // Manually cancelled
    Error         // Failed to submit
}
```

#### Step 2: Add State Tracking to Ledger Interface
**Modify file**: `src/RamStockAlerts.Execution/Interfaces/IExecutionLedger.cs`

Add methods:
```csharp
/// <summary>
/// Get count of submitted (non-rejected) intents today.
/// </summary>
int GetSubmittedIntentCountToday(DateTimeOffset now);

/// <summary>
/// Get count of submitted (non-rejected) brackets today.
/// </summary>
int GetSubmittedBracketCountToday(DateTimeOffset now);

/// <summary>
/// Get count of currently open brackets (entry filled, not yet closed).
/// </summary>
int GetOpenBracketCount();

/// <summary>
/// Update bracket state when order status changes.
/// </summary>
void UpdateBracketState(Guid entryIntentId, BracketState newState);
```

#### Step 3: Update InMemoryExecutionLedger
**Modify file**: `src/RamStockAlerts.Execution/Storage/InMemoryExecutionLedger.cs`

Add tracking:
```csharp
private readonly ConcurrentDictionary<Guid, BracketState> _bracketStates = new();

public void RecordResult(Guid intentId, ExecutionResult result)
{
    // Existing code...
    
    // Track initial bracket state based on result
    if (result.Status == ExecutionStatus.Submitted)
    {
        _bracketStates.TryAdd(intentId, BracketState.Pending);
    }
    else if (result.Status == ExecutionStatus.Rejected || result.Status == ExecutionStatus.Error)
    {
        _bracketStates.TryAdd(intentId, BracketState.Error);
    }
}

public int GetSubmittedIntentCountToday(DateTimeOffset now)
{
    var today_start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
    var today_end = today_start.AddDays(1);
    
    var results = GetResults();
    var submittedIds = results
        .Where(r => r.Status == ExecutionStatus.Submitted && r.TimestampUtc >= today_start && r.TimestampUtc < today_end)
        .Select(r => r.IntentId)
        .ToHashSet();
    
    return submittedIds.Count;
}

public int GetSubmittedBracketCountToday(DateTimeOffset now)
{
    var today_start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
    var today_end = today_start.AddDays(1);
    
    var results = GetResults();
    var brackets = GetBrackets();
    
    return brackets.Count(b => 
    {
        var entryResult = results.FirstOrDefault(r => r.IntentId == b.Entry.IntentId);
        return entryResult?.Status == ExecutionStatus.Submitted 
            && b.Entry.CreatedUtc >= today_start 
            && b.Entry.CreatedUtc < today_end;
    });
}

public int GetOpenBracketCount()
{
    return _bracketStates.Values.Count(s => s == BracketState.Open);
}

public void UpdateBracketState(Guid entryIntentId, BracketState newState)
{
    _bracketStates[entryIntentId] = newState;
    // Log state transition
}
```

#### Step 4: Update RiskManagerV0 to Use New Methods
**Modify file**: `src/RamStockAlerts.Execution/Risk/RiskManagerV0.cs`

Replace `CountOrdersToday`:
```csharp
private int CountOrdersToday(IExecutionLedger ledger, DateTimeOffset now)
{
    return ledger.GetSubmittedIntentCountToday(now);
}
```

Replace `CountBracketsToday`:
```csharp
private int CountBracketsToday(IExecutionLedger ledger, DateTimeOffset now)
{
    return ledger.GetSubmittedBracketCountToday(now);
}
```

Replace `CountOpenPositions`:
```csharp
private int CountOpenPositions(IExecutionLedger ledger, DateTimeOffset now)
{
    return ledger.GetOpenBracketCount();
}
```

#### Step 5: Wire Order Status to Bracket State Updates
When `OrderStateTracker` receives fills/cancellations, call `ledger.UpdateBracketState()`:
- Entry filled → `BracketState.Open`
- Stop filled → `BracketState.ClosedLoss`
- TP filled → `BracketState.ClosedWin`
- Entry cancelled → `BracketState.Cancelled`

#### Step 6: Add Tests
```csharp
[Fact]
public void GetSubmittedIntentCountToday_ExcludesRejected()
{
    var ledger = new InMemoryExecutionLedger();
    var intent = CreateIntent();
    
    ledger.RecordIntent(intent);
    ledger.RecordResult(intent.IntentId, new ExecutionResult { Status = ExecutionStatus.Rejected });
    
    Assert.Equal(0, ledger.GetSubmittedIntentCountToday(DateTimeOffset.UtcNow));
}

[Fact]
public void GetOpenBracketCount_TracksLifecycle()
{
    var ledger = new InMemoryExecutionLedger();
    var bracket = CreateBracket();
    
    ledger.RecordBracket(bracket);
    ledger.RecordResult(bracket.Entry.IntentId, new ExecutionResult { Status = ExecutionStatus.Submitted });
    Assert.Equal(0, ledger.GetOpenBracketCount()); // Still pending
    
    ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.Open);
    Assert.Equal(1, ledger.GetOpenBracketCount());
    
    ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedWin);
    Assert.Equal(0, ledger.GetOpenBracketCount());
}
```

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~ExecutionLedger"
```

---

## 5. Hard Rejection Gates (M, 2h)

### Problem Statement
The `OrderFlowSignalValidator` documents thresholds but they're not enforced as hard gates before signal acceptance:
- SpoofScore < 0.3 (signals fire, but spoofed orders waste capacity)
- TapeAcceleration ≥ 2.0 (weak tape = bad entry)
- WallPersistence ≥ 1000ms (flashing walls = fake liquidity)

Currently, these affect `IsBuyLiquidityFailure`/`IsSellLiquidityFailure` but not as hard pre-acceptance gates.

### Current State
**File**: `src/RamStockAlerts/Engine/OrderFlowSignalValidator.cs` (lines 10-16)
```csharp
/// Signals only when:
/// - QueueImbalance ≥ 2.8 (or ≤ 0.35 for sell)
/// - WallPersistence ≥ 1000ms
/// - AbsorptionRate > threshold
/// - SpoofScore < 0.3 (likely not spoofing)
/// - TapeAcceleration ≥ 2.0
```

**File**: `src/RamStockAlerts/Engine/OrderFlowMetrics.cs` (lines 163-171)
```csharp
// Validate: QI, wall persistence, tape acceleration, spoof score
// - TapeAcceleration >= 2.0 (sudden trade rate spike)
bool tapeAccelerating = snapshot.TapeAcceleration >= TAPE_ACCELERATION_THRESHOLD;
```

### Implementation Plan

#### Step 1: Add Configurable Thresholds
**Modify file**: `src/RamStockAlerts/appsettings.json`

Add under `Signals` section:
```json
"Signals": {
    "HardGates": {
        "MaxSpoofScore": 0.3,
        "MinTapeAcceleration": 2.0,
        "MinWallPersistenceMs": 1000
    }
}
```

#### Step 2: Create Hard Gate Checker
**Modify file**: `src/RamStockAlerts/Engine/OrderFlowSignalValidator.cs`

Add configuration and gate checking:
```csharp
public sealed class HardGateConfig
{
    public decimal MaxSpoofScore { get; set; } = 0.3m;
    public decimal MinTapeAcceleration { get; set; } = 2.0m;
    public long MinWallPersistenceMs { get; set; } = 1000;
}

public sealed record HardGateResult(bool Passed, string? FailedGate, string? Details);

public HardGateResult CheckHardGates(OrderFlowMetrics.MetricSnapshot snapshot, bool isBuy)
{
    var wallAge = isBuy ? snapshot.BidWallAgeMs : snapshot.AskWallAgeMs;
    
    // Gate 1: Spoof Score
    if (snapshot.SpoofScore >= _hardGateConfig.MaxSpoofScore)
    {
        return new HardGateResult(false, "SpoofScore", 
            $"SpoofScore={snapshot.SpoofScore:F2} >= {_hardGateConfig.MaxSpoofScore}");
    }
    
    // Gate 2: Tape Acceleration
    if (snapshot.TapeAcceleration < _hardGateConfig.MinTapeAcceleration)
    {
        return new HardGateResult(false, "TapeAcceleration",
            $"TapeAccel={snapshot.TapeAcceleration:F1} < {_hardGateConfig.MinTapeAcceleration}");
    }
    
    // Gate 3: Wall Persistence
    if (wallAge < _hardGateConfig.MinWallPersistenceMs)
    {
        return new HardGateResult(false, "WallPersistence",
            $"WallAge={wallAge}ms < {_hardGateConfig.MinWallPersistenceMs}ms");
    }
    
    return new HardGateResult(true, null, null);
}
```

#### Step 3: Integrate Hard Gates into EvaluateDecision
**Modify file**: `src/RamStockAlerts/Engine/OrderFlowSignalValidator.cs`

In `EvaluateDecision` method, after direction is determined (around line 105):
```csharp
// Check hard gates before proceeding
var hardGateResult = CheckHardGates(snapshot, isBuyCandidate);
if (!hardGateResult.Passed)
{
    return new OrderFlowSignalDecision(
        HasCandidate: true, 
        Accepted: false, 
        RejectionReason: $"HardGate:{hardGateResult.FailedGate}", 
        Signal: signal, 
        Snapshot: snapshot, 
        Direction: direction);
}
```

#### Step 4: Add Hard Gate Reasons to SignalCoordinator
**Modify file**: `src/RamStockAlerts/Services/Signals/SignalCoordinator.cs`

Add to `MapReason` method:
```csharp
"HardGate:SpoofScore" => HardRejectReason.SpoofSuspected,
"HardGate:TapeAcceleration" => HardRejectReason.TapeAccelerationInsufficient,
"HardGate:WallPersistence" => HardRejectReason.WallPersistenceInsufficient,
```

Add new enum values to `HardRejectReason`:
```csharp
TapeAccelerationInsufficient,
WallPersistenceInsufficient,
```

#### Step 5: Add Telemetry for Gate Rejections
Log each hard gate rejection:
```csharp
_logger.LogInformation("[HardGate] {Symbol} rejected: {Gate} - {Details}", 
    snapshot.Symbol, hardGateResult.FailedGate, hardGateResult.Details);
```

Add to rejection summary counters.

#### Step 6: Add Tests
**Create file**: `tests/RamStockAlerts.Tests/HardGateTests.cs`
```csharp
[Fact]
public void CheckHardGates_HighSpoofScore_Fails()
{
    var snapshot = CreateSnapshot(spoofScore: 0.5m);
    var result = _validator.CheckHardGates(snapshot, isBuy: true);
    
    Assert.False(result.Passed);
    Assert.Equal("SpoofScore", result.FailedGate);
}

[Fact]
public void CheckHardGates_LowTapeAccel_Fails()
{
    var snapshot = CreateSnapshot(tapeAccel: 1.5m);
    var result = _validator.CheckHardGates(snapshot, isBuy: true);
    
    Assert.False(result.Passed);
    Assert.Equal("TapeAcceleration", result.FailedGate);
}

[Fact]
public void CheckHardGates_ShortWallAge_Fails()
{
    var snapshot = CreateSnapshot(bidWallAgeMs: 500);
    var result = _validator.CheckHardGates(snapshot, isBuy: true);
    
    Assert.False(result.Passed);
    Assert.Equal("WallPersistence", result.FailedGate);
}

[Fact]
public void CheckHardGates_AllPass_Succeeds()
{
    var snapshot = CreateSnapshot(spoofScore: 0.1m, tapeAccel: 3.0m, bidWallAgeMs: 1500);
    var result = _validator.CheckHardGates(snapshot, isBuy: true);
    
    Assert.True(result.Passed);
}
```

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~HardGate"
```

Expected log output when gate fails:
```
[HardGate] AAPL rejected: SpoofScore - SpoofScore=0.45 >= 0.3
```

---

## 6. Post-Signal Monitor (L, 1-2d)

### Problem Statement
After a signal is accepted, market conditions can deteriorate:
- Spread blows out (liquidity disappears)
- Tape slows down (momentum dies)
- Order remains unfilled as edge decays

Need to monitor accepted signals and cancel orders when conditions degrade.

### Current State
**File**: `src/RamStockAlerts/Services/Signals/SignalCoordinator.cs` (lines 49-53)
```csharp
// Phase 3.1: Post-Signal Quality Monitoring
private readonly ConcurrentDictionary<string, AcceptedSignalTracker> _acceptedSignals = new();
private readonly bool _postSignalMonitoringEnabled;
private readonly double _tapeSlowdownThreshold; // 50% = 0.5
private readonly double _spreadBlowoutThreshold; // 50% = 0.5
```

The scaffolding exists but `MonitorPostSignalQuality` and order cancellation are not implemented.

### Implementation Plan

#### Step 1: Define AcceptedSignalTracker
**Add to SignalCoordinator.cs** or create new file:
```csharp
public sealed class AcceptedSignalTracker
{
    public required string Symbol { get; init; }
    public required string Direction { get; init; }
    public required Guid DecisionId { get; init; }
    public required long AcceptedAtMs { get; init; }
    public required decimal BaselineSpread { get; init; }
    public required int BaselineTapeVelocity { get; init; }
    public required List<string> BrokerOrderIds { get; init; }
    
    // Monitoring state
    public int ConsecutiveSpreadBlowouts { get; set; } = 0;
    public int ConsecutiveTapeSlowdowns { get; set; } = 0;
    public bool CancelRequested { get; set; } = false;
    public DateTimeOffset? CancelRequestedUtc { get; set; }
}
```

#### Step 2: Implement TrackAcceptedSignal
**Modify file**: `src/RamStockAlerts/Services/Signals/SignalCoordinator.cs`

```csharp
private void TrackAcceptedSignal(
    string symbol, 
    string direction, 
    Guid decisionId, 
    decimal baselineSpread, 
    int baselineTapeVelocity, 
    int baselineOppositeVelocity,
    long nowMs,
    List<string>? brokerOrderIds = null)
{
    if (!_postSignalMonitoringEnabled)
        return;
    
    var tracker = new AcceptedSignalTracker
    {
        Symbol = symbol,
        Direction = direction,
        DecisionId = decisionId,
        AcceptedAtMs = nowMs,
        BaselineSpread = baselineSpread,
        BaselineTapeVelocity = baselineTapeVelocity,
        BrokerOrderIds = brokerOrderIds ?? new List<string>()
    };
    
    _acceptedSignals[symbol] = tracker;
    
    _logger.LogInformation("[PostSignal] Tracking {Symbol} {Direction} baseline: spread={Spread} tapeVel={Velocity}",
        symbol, direction, baselineSpread, baselineTapeVelocity);
}
```

#### Step 3: Implement MonitorPostSignalQuality
**Modify file**: `src/RamStockAlerts/Services/Signals/SignalCoordinator.cs`

```csharp
private void MonitorPostSignalQuality(OrderBookState book, long nowMs)
{
    if (!_postSignalMonitoringEnabled)
        return;
    
    if (!_acceptedSignals.TryGetValue(book.Symbol, out var tracker))
        return;
    
    if (tracker.CancelRequested)
        return;
    
    var snapshot = _metrics.GetLatestSnapshot(book.Symbol);
    if (snapshot is null)
        return;
    
    var currentSpread = book.Spread;
    var currentTapeVelocity = GetCurrentTapeVelocity(book, tracker.Direction);
    
    // Check spread blowout
    if (tracker.BaselineSpread > 0)
    {
        var spreadRatio = currentSpread / tracker.BaselineSpread;
        if (spreadRatio > 1.0m + (decimal)_spreadBlowoutThreshold)
        {
            tracker.ConsecutiveSpreadBlowouts++;
            _logger.LogWarning("[PostSignal] {Symbol} spread blowout {Count}/3: {Current} vs baseline {Baseline} (ratio {Ratio:F2})",
                book.Symbol, tracker.ConsecutiveSpreadBlowouts, currentSpread, tracker.BaselineSpread, spreadRatio);
        }
        else
        {
            tracker.ConsecutiveSpreadBlowouts = 0;
        }
    }
    
    // Check tape slowdown
    if (tracker.BaselineTapeVelocity > 0)
    {
        var velocityRatio = (double)currentTapeVelocity / tracker.BaselineTapeVelocity;
        if (velocityRatio < (1.0 - _tapeSlowdownThreshold))
        {
            tracker.ConsecutiveTapeSlowdowns++;
            _logger.LogWarning("[PostSignal] {Symbol} tape slowdown {Count}/3: {Current} vs baseline {Baseline} (ratio {Ratio:F2})",
                book.Symbol, tracker.ConsecutiveTapeSlowdowns, currentTapeVelocity, tracker.BaselineTapeVelocity, velocityRatio);
        }
        else
        {
            tracker.ConsecutiveTapeSlowdowns = 0;
        }
    }
    
    // Trigger cancellation after 3 consecutive degradations
    const int CancelThreshold = 3;
    if (tracker.ConsecutiveSpreadBlowouts >= CancelThreshold || 
        tracker.ConsecutiveTapeSlowdowns >= CancelThreshold)
    {
        RequestCancelSignal(tracker, nowMs);
    }
}

private int GetCurrentTapeVelocity(OrderBookState book, string direction)
{
    // Get trades in last 3 seconds on the relevant side
    return direction.Equals("BUY", StringComparison.OrdinalIgnoreCase)
        ? book.GetAskSideTrades(3000).Count
        : book.GetBidSideTrades(3000).Count;
}

private void RequestCancelSignal(AcceptedSignalTracker tracker, long nowMs)
{
    if (tracker.CancelRequested)
        return;
    
    tracker.CancelRequested = true;
    tracker.CancelRequestedUtc = DateTimeOffset.UtcNow;
    
    var reason = tracker.ConsecutiveSpreadBlowouts >= 3 ? "SpreadBlowout" : "TapeSlowdown";
    
    _logger.LogWarning("[PostSignal] Requesting cancellation for {Symbol} {Direction}: {Reason}",
        tracker.Symbol, tracker.Direction, reason);
    
    // Fire cancellation via execution service
    if (_executionService is not null && tracker.BrokerOrderIds.Count > 0)
    {
        foreach (var orderId in tracker.BrokerOrderIds)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _executionService.CancelAsync(orderId);
                    _logger.LogInformation("[PostSignal] Cancel result for order {OrderId}: {Status}",
                        orderId, result.Status);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PostSignal] Failed to cancel order {OrderId}", orderId);
                }
            });
        }
    }
    
    // Remove from tracking
    _acceptedSignals.TryRemove(tracker.Symbol, out _);
    
    // Journal the cancellation
    JournalCancellation(tracker, reason, nowMs);
}
```

#### Step 4: Add Cancellation Journaling
```csharp
private void JournalCancellation(AcceptedSignalTracker tracker, string reason, long nowMs)
{
    var entry = new TradeJournalEntry
    {
        DecisionId = Guid.NewGuid(),
        Symbol = tracker.Symbol,
        Direction = tracker.Direction,
        DecisionOutcome = "Cancelled",
        RejectionReason = $"PostSignal:{reason}",
        DecisionTimestampUtc = DateTimeOffset.UtcNow,
        SessionId = _sessionId,
        Mode = TradingModeLabel,
        DataQualityFlags = new List<string> { $"OriginalDecisionId:{tracker.DecisionId}" }
    };
    
    EnqueueEntry(entry);
}
```

#### Step 5: Wire BrokerOrderIds from Execution
When auto-execution succeeds, pass order IDs to tracker:
**Modify `TryAutoExecuteFromSignal`**:
```csharp
var result = await _executionService.ExecuteAsync(bracket);
if (result.Status == ExecutionStatus.Submitted && result.BrokerOrderIds.Count > 0)
{
    // Update tracker with actual order IDs
    if (_acceptedSignals.TryGetValue(entry.Symbol!, out var tracker))
    {
        tracker.BrokerOrderIds.AddRange(result.BrokerOrderIds);
    }
}
```

#### Step 6: Add Timeout for Stale Trackers
Clean up trackers after 5 minutes (configurable):
```csharp
private void CleanupStaleTrackers(long nowMs)
{
    const long MaxTrackerAgeMs = 5 * 60 * 1000; // 5 minutes
    
    var stale = _acceptedSignals
        .Where(kvp => nowMs - kvp.Value.AcceptedAtMs > MaxTrackerAgeMs)
        .Select(kvp => kvp.Key)
        .ToList();
    
    foreach (var symbol in stale)
    {
        if (_acceptedSignals.TryRemove(symbol, out var tracker))
        {
            _logger.LogDebug("[PostSignal] Removed stale tracker for {Symbol} (age={AgeMs}ms)",
                symbol, nowMs - tracker.AcceptedAtMs);
        }
    }
}
```

Call from `ProcessSnapshot` or the summary timer.

#### Step 7: Add Configuration Options
**Modify `appsettings.json`**:
```json
"Signals": {
    "PostSignalMonitoringEnabled": true,
    "SpreadBlowoutThreshold": 0.5,
    "TapeSlowdownThreshold": 0.5,
    "PostSignalConsecutiveThreshold": 3,
    "PostSignalMaxAgeMs": 300000
}
```

#### Step 8: Add Tests
**Create file**: `tests/RamStockAlerts.Tests/PostSignalMonitorTests.cs`
```csharp
[Fact]
public void MonitorPostSignalQuality_SpreadBlowout_RequestsCancel()
{
    // Setup: Accept signal with spread=0.02
    _coordinator.TrackAcceptedSignal("AAPL", "BUY", Guid.NewGuid(), 0.02m, 10, 5, 0);
    
    // Simulate 3 consecutive blowouts (spread > 0.03 = 50% increase)
    for (int i = 0; i < 3; i++)
    {
        var book = CreateBook("AAPL", spread: 0.04m);
        _coordinator.ProcessSnapshot(book, i * 1000);
    }
    
    // Verify cancellation was requested
    Assert.True(_fakeExecutionService.CancelCalled);
}

[Fact]
public void MonitorPostSignalQuality_TapeSlowdown_RequestsCancel()
{
    _coordinator.TrackAcceptedSignal("MSFT", "BUY", Guid.NewGuid(), 0.01m, 20, 10, 0);
    
    // Simulate tape dying (velocity < 10 = 50% drop from baseline 20)
    for (int i = 0; i < 3; i++)
    {
        var book = CreateBook("MSFT", askTrades3s: 5);
        _coordinator.ProcessSnapshot(book, i * 1000);
    }
    
    Assert.True(_fakeExecutionService.CancelCalled);
}

[Fact]
public void MonitorPostSignalQuality_ConditionsImprove_ResetsCounter()
{
    _coordinator.TrackAcceptedSignal("GOOGL", "SELL", Guid.NewGuid(), 0.02m, 15, 8, 0);
    
    // 2 blowouts then recovery
    _coordinator.ProcessSnapshot(CreateBook("GOOGL", spread: 0.04m), 1000);
    _coordinator.ProcessSnapshot(CreateBook("GOOGL", spread: 0.04m), 2000);
    _coordinator.ProcessSnapshot(CreateBook("GOOGL", spread: 0.02m), 3000); // Recovery
    _coordinator.ProcessSnapshot(CreateBook("GOOGL", spread: 0.04m), 4000);
    _coordinator.ProcessSnapshot(CreateBook("GOOGL", spread: 0.04m), 5000);
    
    // Should NOT cancel (counter reset at step 3)
    Assert.False(_fakeExecutionService.CancelCalled);
}
```

### Verification
```bash
dotnet build RamStockAlerts.sln
dotnet test --filter "FullyQualifiedName~PostSignalMonitor"
```

Expected log output:
```
[PostSignal] Tracking AAPL BUY baseline: spread=0.0200 tapeVel=15
[PostSignal] AAPL spread blowout 1/3: 0.0350 vs baseline 0.0200 (ratio 1.75)
[PostSignal] AAPL spread blowout 2/3: 0.0380 vs baseline 0.0200 (ratio 1.90)
[PostSignal] AAPL spread blowout 3/3: 0.0400 vs baseline 0.0200 (ratio 2.00)
[PostSignal] Requesting cancellation for AAPL BUY: SpreadBlowout
[PostSignal] Cancel result for order 12345: Cancelled
```

---

---

## 7. Additional Enhancements (Bookmarked)

These are supplementary improvements identified during planning. They can be implemented alongside or after the core features.

### 7.1 Pre-Fill Monitoring (Enhancement to #6)

**Problem**: The Post-Signal Monitor currently triggers *after* signal acceptance, but real edge decay can happen *before* the entry fill arrives.

**Enhancement**:
Track the period between signal acceptance → entry fill separately from entry fill → exit.

**Implementation**:
```csharp
public enum MonitorPhase
{
    AwaitingFill,   // Signal accepted, waiting for entry fill
    PositionOpen    // Entry filled, monitoring until exit
}

public sealed class AcceptedSignalTracker
{
    // ... existing fields ...
    public MonitorPhase Phase { get; set; } = MonitorPhase.AwaitingFill;
    public DateTimeOffset? EntryFilledUtc { get; set; }
}
```

In `MonitorPostSignalQuality`:
```csharp
// Phase 1: Pre-fill monitoring (stricter thresholds)
if (tracker.Phase == MonitorPhase.AwaitingFill)
{
    // Cancel faster if spread blows out before we're even filled
    const int PreFillCancelThreshold = 2; // vs 3 for post-fill
    if (tracker.ConsecutiveSpreadBlowouts >= PreFillCancelThreshold)
    {
        RequestCancelSignal(tracker, nowMs, "PreFillSpreadBlowout");
        return;
    }
}
```

Wire phase transition when `OrderStateTracker` reports entry fill:
```csharp
public void OnEntryFilled(string symbol)
{
    if (_acceptedSignals.TryGetValue(symbol, out var tracker))
    {
        tracker.Phase = MonitorPhase.PositionOpen;
        tracker.EntryFilledUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation("[PostSignal] {Symbol} entry filled, transitioning to PositionOpen phase", symbol);
    }
}
```

---

### 7.2 Broker Failure Contingencies (Enhancement to #1)

**Problem**: If IBKR connection drops or callbacks stop arriving, orders may be in limbo with no visibility.

**Enhancement**:
Add timeout-based soft alerts for orders that don't receive status updates.

**Implementation**:
Add to `OrderStateTracker`:
```csharp
private readonly TimeSpan _orderStatusTimeout = TimeSpan.FromSeconds(30);
private readonly TimeSpan _fillTimeout = TimeSpan.FromMinutes(2);

public IReadOnlyList<int> GetStaleOrders()
{
    var now = DateTimeOffset.UtcNow;
    return _orders.Values
        .Where(o => o.Status == BrokerOrderStatus.Submitted 
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
            _logger.LogWarning("[OrderTracker] STALE ORDER ALERT: Order {OrderId} ({Symbol}) no status update for {Elapsed}s",
                orderId, order.Symbol, (DateTimeOffset.UtcNow - order.LastUpdateUtc).TotalSeconds);
        }
    }
}
```

Call from a periodic timer (every 15s):
```csharp
_staleOrderTimer = new Timer(_ => _orderTracker.CheckForStaleOrders(), null, 
    TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
```

**Future Ops Milestone**: Add Discord alert for stale orders, auto-query IBKR `reqOpenOrders()` for reconciliation.

---

### 7.3 Throttle Post-Signal Monitoring Load (Enhancement to #6)

**Problem**: During volatile tape with many symbols, per-snapshot monitoring could starve signal detection or IBKR callback processing.

**Enhancement**:
Batch monitoring checks and add per-symbol throttling.

**Implementation**:
```csharp
private readonly ConcurrentDictionary<string, long> _lastMonitorCheckMs = new();
private const long MonitorThrottleMs = 500; // Max once per 500ms per symbol

private void MonitorPostSignalQuality(OrderBookState book, long nowMs)
{
    if (!_postSignalMonitoringEnabled)
        return;
    
    if (!_acceptedSignals.TryGetValue(book.Symbol, out var tracker))
        return;
    
    // Throttle monitoring per symbol
    if (_lastMonitorCheckMs.TryGetValue(book.Symbol, out var lastCheck) 
        && nowMs - lastCheck < MonitorThrottleMs)
    {
        return;
    }
    _lastMonitorCheckMs[book.Symbol] = nowMs;
    
    // ... rest of monitoring logic ...
}
```

Add batch processing for high-symbol-count scenarios:
```csharp
// Alternative: Move monitoring to dedicated timer instead of per-snapshot
private void BatchMonitorAcceptedSignals()
{
    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var symbolsToCheck = _acceptedSignals.Keys.Take(10).ToList(); // Process 10 at a time
    
    foreach (var symbol in symbolsToCheck)
    {
        if (_subscriptionManager.TryGetOrderBook(symbol, out var book))
        {
            MonitorPostSignalQualityCore(book, nowMs);
        }
    }
}
```

---

### 7.4 Soft Kill-Switch Toggle (New Feature)

**Problem**: If unusual behavior is observed mid-day, you may want to stop new trades without a full system shutdown.

**Enhancement**:
Add a "monitor-only" mode that continues signal detection and logging but blocks execution.

**Implementation**:

Add to `ExecutionOptions`:
```csharp
/// <summary>
/// Soft kill switch: signals still fire and journal, but no orders are placed.
/// Can be toggled via API or Discord command mid-session.
/// </summary>
public bool MonitorOnly { get; set; } = false;
```

Add runtime toggle capability:
```csharp
// In AdminController or dedicated endpoint
[HttpPost("execution/monitor-only")]
public IActionResult SetMonitorOnly([FromQuery] bool enabled)
{
    _executionOptions.MonitorOnly = enabled;
    _logger.LogWarning("[Admin] Monitor-only mode {State} by operator", enabled ? "ENABLED" : "DISABLED");
    return Ok(new { monitorOnly = enabled });
}
```

Check in `ExecutionService.ExecuteAsync`:
```csharp
public async Task<ExecutionResult> ExecuteAsync(BracketIntent intent, CancellationToken ct = default)
{
    if (_options.MonitorOnly)
    {
        _logger.LogInformation("[Execution] Monitor-only mode active. Skipping execution for {Symbol}", intent.Entry?.Symbol);
        return new ExecutionResult
        {
            IntentId = intent.Entry?.IntentId ?? Guid.Empty,
            Status = ExecutionStatus.Rejected,
            RejectionReason = "MonitorOnlyMode",
            BrokerName = _brokerClient.Name,
            TimestampUtc = DateTimeOffset.UtcNow
        };
    }
    
    // ... normal execution ...
}
```

**Optional Discord integration**:
```csharp
// React to Discord command: !monitor-only on/off
if (command == "monitor-only")
{
    var enabled = args == "on";
    _executionOptions.MonitorOnly = enabled;
    await ReplyAsync($"🛑 Monitor-only mode: **{(enabled ? "ON" : "OFF")}**");
}
```

---

### 7.5 Tag Trades by Quality Bands (Future Analytics)

**Problem**: Once outcome tracking is live, we need to analyze which signal quality bands actually carry edge.

**Enhancement**:
Tag accepted signals with score bands for later cohort analysis.

**Implementation**:

Add to `TradeJournalEntry`:
```csharp
/// <summary>
/// Quality band for cohort analysis: "Elite" (9.0-10.0), "Strong" (8.0-8.9), "Standard" (7.0-7.9)
/// </summary>
public string? QualityBand { get; set; }
```

Assign at signal acceptance:
```csharp
private static string GetQualityBand(decimal? score)
{
    return score switch
    {
        >= 9.0m => "Elite",
        >= 8.0m => "Strong",
        >= 7.0m => "Standard",
        >= 6.0m => "Marginal",
        _ => "Low"
    };
}

// In FinalizeRankedDecisions, when accepted:
entry.QualityBand = GetQualityBand(entry.DecisionInputs?.Score);
```

Add to `TradeOutcome`:
```csharp
public string? QualityBand { get; set; }
```

Carry forward in `TradeOutcomeLabeler`:
```csharp
outcome.QualityBand = journalEntry.QualityBand;
```

**Analytics query example** (for DailyRollupReporter):
```csharp
var bandStats = outcomes
    .Where(o => o.QualityBand != null)
    .GroupBy(o => o.QualityBand)
    .Select(g => new
    {
        Band = g.Key,
        Count = g.Count(),
        WinRate = g.Count(o => o.IsWin == true) / (decimal)g.Count(),
        AvgR = g.Where(o => o.RiskMultiple.HasValue).Average(o => o.RiskMultiple!.Value)
    })
    .OrderByDescending(x => x.Band);

// Output:
// Elite:    12 trades, 75% win, +1.8R avg
// Strong:   28 trades, 64% win, +1.2R avg
// Standard: 45 trades, 55% win, +0.6R avg
```

**Not urgent** - implement after outcome tracking is validated.

---

## Summary: Dependency Order

For optimal implementation order:

```
1. IBKR Order Status Callbacks (FIRST - foundation for everything else)
   ├──→ 7.2 Broker Failure Contingencies (enhancement)
   ↓
2. Fix P&L Calculation (can be done in parallel with #1)
   ↓
3. Fix Ledger Counting (depends on #1 for BracketState updates)
   ↓
4. Daily Loss Limit Enforcement (depends on #1 for realized P&L)
   ↓
5. Hard Rejection Gates (independent, can be done anytime)
   ↓
6. Post-Signal Monitor (depends on #1 for order cancellation)
   ├──→ 7.1 Pre-Fill Monitoring (enhancement)
   └──→ 7.3 Throttle Monitoring Load (enhancement)

Independent (implement anytime):
├── 7.4 Soft Kill-Switch Toggle
└── 7.5 Tag Trades by Quality Bands (after outcome tracking works)
```

## Verification Commands

After implementing all features:
```bash
# Full build
dotnet build RamStockAlerts.sln

# All tests
dotnet test RamStockAlerts.sln

# Feature-specific tests
dotnet test --filter "FullyQualifiedName~OrderStateTracker"
dotnet test --filter "FullyQualifiedName~OutcomeLabeling"
dotnet test --filter "FullyQualifiedName~RiskManagerV0"
dotnet test --filter "FullyQualifiedName~ExecutionLedger"
dotnet test --filter "FullyQualifiedName~HardGate"
dotnet test --filter "FullyQualifiedName~PostSignalMonitor"

# Verify script
powershell -File scripts/verify.ps1
```
