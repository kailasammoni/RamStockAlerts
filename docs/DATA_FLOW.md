# Data Flow Diagrams

This document describes how data flows through the RamStockAlerts system, from market data ingestion to trade execution and performance tracking.

---

## Table of Contents

1. [Market Data Ingestion Flow](#market-data-ingestion-flow)
2. [Signal Generation and Validation Flow](#signal-generation-and-validation-flow)
3. [Order Execution Flow](#order-execution-flow)
4. [Performance Tracking Flow](#performance-tracking-flow)
5. [Database Schema](#database-schema)
6. [State Transitions](#state-transitions)

---

## Market Data Ingestion Flow

### Overview
Market data flows from IBKR TWS through the IBKRClient into various tracking and filtering services.

```
┌─────────────────────────────────────────────────────────────┐
│                    IBKR Trader Workstation                  │
│              (Running on localhost:7497)                    │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ TCP Connection
                 │ (IBKR API Protocol)
                 ▼
┌─────────────────────────────────────────────────────────────┐
│                      IBKRClient                             │
│              (Feeds/IBKRClient.cs)                          │
│                                                             │
│  ├─ Market Depth Subscription (Level 2)                    │
│  ├─ Time & Sales Subscription (Tape)                       │
│  ├─ Quote Subscription (Bid/Ask/Last)                      │
│  └─ VWAP Calculation                                        │
└────────┬────────────────┬──────────────┬───────────────────┘
         │                │              │
         │                │              │
         ▼                ▼              ▼
┌────────────────┐  ┌──────────────┐  ┌──────────────────┐
│DepthDeltaTracker│  │DepthUniverse │  │OrderFlowSignal   │
│                │  │Filter        │  │Service           │
│                │  │              │  │                  │
│Monitors order  │  │Filters symbols│  │Scores trading    │
│book changes    │  │by price, float│  │opportunities     │
│Detects flash   │  │spread, volume │  │0-10 scale        │
│walls           │  │              │  │                  │
└────────────────┘  └──────────────┘  └──────────────────┘
```

### Data Structures

**Level 2 Market Depth Update:**
```csharp
public class MarketDepthUpdate
{
    public string Symbol { get; set; }
    public int Position { get; set; }      // 0-9 (10 levels deep)
    public string MarketMaker { get; set; }
    public int Operation { get; set; }     // 0=Insert, 1=Update, 2=Delete
    public int Side { get; set; }          // 0=Ask, 1=Bid
    public decimal Price { get; set; }
    public int Size { get; set; }
}
```

**Order Book Snapshot:**
```csharp
public class OrderBook
{
    public string Symbol { get; set; }
    public DateTime Timestamp { get; set; }
    public List<PriceLevel> Bids { get; set; }  // Sorted descending
    public List<PriceLevel> Asks { get; set; }  // Sorted ascending
    public decimal Spread => Asks[0].Price - Bids[0].Price;
    public decimal BidAskRatio { get; set; }
}

public class PriceLevel
{
    public decimal Price { get; set; }
    public int Size { get; set; }
}
```

### Flow Details

1. **Connection Establishment**
   - IBKRClient connects to TWS on startup
   - Subscribes to market data for symbols in the trading universe
   - Maintains persistent connection with heartbeat

2. **Market Depth Stream**
   - IBKR sends depth updates in real-time (50-100 updates/second per symbol)
   - Each update: position, side (bid/ask), price, size, operation
   - IBKRClient reconstructs full order book from updates

3. **Tracking and Filtering**
   - **DepthDeltaTracker**: Receives order book snapshots every 100ms
     - Tracks appearance/disappearance of large walls
     - Calculates bid-ask ratio over rolling 5-second window
     - Detects flash walls (orders that vanish within 2-5 seconds)

   - **DepthUniverseFilter**: Validates symbols in real-time
     - Checks if price still within $2-$20 range
     - Verifies spread within $0.02-$0.05
     - Removes symbols that no longer meet criteria

   - **OrderFlowSignalService**: Scores opportunities
     - Combines order book data with tape data
     - Calculates signal score (0-10)
     - Emits scored signals for validation

---

## Signal Generation and Validation Flow

### Overview
Raw order flow data is transformed into validated trading signals through a multi-gate scoring and validation pipeline.

```
┌─────────────────────────────────────────────────────────────┐
│             OrderFlowSignalService                          │
│          (Services/OrderFlowSignalService.cs)               │
│                                                             │
│  Input:                                                     │
│  ├─ Order Book Snapshot                                    │
│  ├─ Tape Data (Time & Sales)                               │
│  ├─ VWAP                                                    │
│  └─ Historical Metrics                                      │
│                                                             │
│  Scoring Metrics:                                           │
│  ├─ Bid-Ask Ratio (weight: 2.5)                            │
│  ├─ Spread Compression (weight: 1.5)                       │
│  ├─ Tape Velocity (weight: 2.0)                            │
│  ├─ Level 2 Depth (weight: 2.0)                            │
│  └─ VWAP Reclaim (weight: 1.5)                             │
│                                                             │
│  Raw Score: 0-10                                            │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Raw Signal (unvalidated)
                 ▼
┌─────────────────────────────────────────────────────────────┐
│          SignalValidationEngine                             │
│          (Engine/SignalValidationEngine.cs)                 │
│                                                             │
│  Gate Pipeline (sequential):                               │
│                                                             │
│  1. HardGate                                                │
│     ├─ Spread > $0.05? → REJECT                            │
│     ├─ Price outside $2-$20? → REJECT                      │
│     └─ Float outside 5M-50M? → REJECT                      │
│                                                             │
│  2. AbsorptionReject                                        │
│     ├─ Flash wall detected? → REJECT                       │
│     └─ Fake bid wall pattern? → REJECT                     │
│                                                             │
│  3. SpreadGate                                              │
│     └─ Spread widening? → REDUCE SCORE -0.5                │
│                                                             │
│  4. VolumeGate                                              │
│     └─ Volume below threshold? → REDUCE SCORE -0.3         │
│                                                             │
│  5. FloatGate                                               │
│     └─ Float at extremes (5M or 50M)? → REDUCE SCORE -0.2  │
│                                                             │
│  6. TimeGate                                                │
│     ├─ In dark period (11:30-14:00)? → REJECT              │
│     └─ After hours? → REJECT                               │
│                                                             │
│  7. ContractClassificationGate                              │
│     └─ Not common stock? → REJECT                          │
│                                                             │
│  8. ManipulationGate                                        │
│     ├─ Iceberg selling detected? → REJECT                  │
│     └─ Coordinated bid wall spoofing? → REJECT             │
│                                                             │
│  Final Score: Raw Score + Adjustments                       │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Validated Signal
                 │ (if score ≥ 8.0)
                 ▼
┌─────────────────────────────────────────────────────────────┐
│              RiskManager                                    │
│        (Execution/Risk/RiskManager.cs)                      │
│                                                             │
│  Risk Checks:                                               │
│  ├─ Daily drawdown < $600?                                 │
│  ├─ Position size 400-600 shares?                          │
│  ├─ Risk per trade ≤ 0.25% of account?                     │
│  ├─ Max concurrent positions ≤ 2?                          │
│  └─ Account size $30K-$80K?                                │
│                                                             │
│  Output: Approved Signal + Position Size                   │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Risk-Approved Signal
                 ▼
         [Order Execution Flow]
```

### Gate Trace Example

For a signal on symbol AAPL at 10:00:00 with initial raw score 8.5:

```json
{
  "symbol": "AAPL",
  "timestamp": "2025-01-31T10:00:00Z",
  "rawScore": 8.5,
  "gates": [
    {
      "name": "HardGate",
      "result": "PASS",
      "details": {
        "spread": 0.03,
        "price": 150.00,
        "float": 15500000
      },
      "scoreAdjustment": 0.0
    },
    {
      "name": "AbsorptionReject",
      "result": "PASS",
      "details": {
        "flashWallsDetected": 0,
        "fakeBidWalls": 0
      },
      "scoreAdjustment": 0.0
    },
    {
      "name": "VolumeGate",
      "result": "SOFT_REDUCE",
      "details": {
        "currentVolume": 800000,
        "threshold": 1000000
      },
      "scoreAdjustment": -0.3
    }
  ],
  "finalScore": 8.2,
  "decision": "ACCEPTED"
}
```

---

## Order Execution Flow

### Overview
Validated signals are converted into bracket orders and submitted to IBKR for execution.

```
┌─────────────────────────────────────────────────────────────┐
│              Risk-Approved Signal                           │
│  {symbol: AAPL, score: 8.2, entryPrice: 150.00}            │
└────────────────┬────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────┐
│          BracketTemplateBuilder                             │
│    (Execution/Services/BracketTemplateBuilder.cs)           │
│                                                             │
│  Calculates:                                                │
│  ├─ Position Size: 500 shares (based on risk)              │
│  ├─ Entry Price: $150.00 (market order)                    │
│  ├─ Profit Target: $150.75 (0.5% profit)                   │
│  └─ Stop Loss: $149.80 (0.13% risk = $100 on 500 shares)   │
│                                                             │
│  Creates Order Set:                                         │
│  ├─ Parent Order (Entry)                                   │
│  ├─ Child 1 (Profit Target - conditional on parent fill)   │
│  └─ Child 2 (Stop Loss - conditional on parent fill)       │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Bracket Order Set
                 ▼
┌─────────────────────────────────────────────────────────────┐
│              ExecutionService                               │
│        (Execution/Services/ExecutionService.cs)             │
│                                                             │
│  1. Submit parent order to IBKR                             │
│  2. Wait for confirmation (OrderId assigned)                │
│  3. Submit child orders (linked to parent)                  │
│  4. Monitor order status updates                            │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Orders Submitted
                 ▼
┌─────────────────────────────────────────────────────────────┐
│                  IBKR Execution                             │
│                                                             │
│  Order Lifecycle:                                           │
│  ├─ PendingSubmit → PreSubmitted → Submitted                │
│  ├─ PartiallyFilled (if applicable)                        │
│  └─ Filled                                                  │
│                                                             │
│  Bracket Behavior:                                          │
│  ├─ Entry fills at $150.02 (slight slippage)               │
│  ├─ Profit target activated at $150.75                     │
│  ├─ Stop loss activated at $149.80                         │
│  └─ One-Cancels-All (OCA): When one fills, other cancels   │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Fill Updates
                 ▼
┌─────────────────────────────────────────────────────────────┐
│            OrderStateTracker                                │
│      (Execution/Services/OrderStateTracker.cs)              │
│                                                             │
│  Tracks:                                                    │
│  ├─ Entry fill: 500 shares @ $150.02                       │
│  ├─ Position open time: 10:00:02                           │
│  ├─ Awaiting exit (profit target or stop loss)             │
│  └─ Exit fill: 500 shares @ $150.77 (profit target hit)    │
│                                                             │
│  State Transitions:                                         │
│  Submitted → Filled (entry) → Open Position →              │
│  → Exit Filled → Closed Position                           │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Position Closed Event
                 ▼
┌─────────────────────────────────────────────────────────────┐
│              ExecutionLedger                                │
│        (Execution/Storage/ExecutionLedger.cs)               │
│                                                             │
│  Records:                                                   │
│  ├─ Entry Order: BUY 500 @ $150.02 (commission $2.50)      │
│  ├─ Exit Order: SELL 500 @ $150.77 (commission $2.50)      │
│  ├─ Gross P&L: $375 (500 × $0.75)                          │
│  └─ Net P&L: $370 ($375 - $5 commissions)                  │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ Trade Outcome
                 ▼
         [Performance Tracking Flow]
```

### Order State Diagram

```
┌─────────────┐
│  Signal     │
│ Validated   │
└──────┬──────┘
       │
       ▼
┌─────────────┐
│PendingSubmit│ (Order created, not yet sent to IBKR)
└──────┬──────┘
       │
       ▼
┌─────────────┐
│PreSubmitted │ (Order sent to IBKR, awaiting acknowledgment)
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Submitted  │ (Order acknowledged by IBKR, in market)
└──────┬──────┘
       │
       ├─────────────┐
       │             │
       ▼             ▼
┌─────────────┐  ┌─────────────┐
│ Partially   │  │   Filled    │ (Order completely filled)
│   Filled    │  └──────┬──────┘
└──────┬──────┘         │
       │                │
       └────────┬───────┘
                │
                ▼
         ┌─────────────┐
         │   Position  │ (Holding shares, awaiting exit)
         │    Open     │
         └──────┬──────┘
                │
                ├──────────────┬──────────────┐
                │              │              │
                ▼              ▼              ▼
         ┌─────────────┐ ┌──────────┐ ┌───────────┐
         │Profit Target│ │Stop Loss │ │Time Exit  │
         │   Hit       │ │   Hit    │ │(EOD close)│
         └──────┬──────┘ └────┬─────┘ └─────┬─────┘
                │             │              │
                └─────────────┴──────────────┘
                                │
                                ▼
                         ┌─────────────┐
                         │  Position   │
                         │   Closed    │
                         └──────┬──────┘
                                │
                                ▼
                         ┌─────────────┐
                         │  Outcome    │
                         │  Recorded   │
                         └─────────────┘
```

### Rejection Scenarios

Orders can be rejected at multiple points:

```
Signal Validation → Risk Check → Order Submission → IBKR Acceptance
     │                  │               │                  │
     │                  │               │                  │
  [REJECT]          [REJECT]        [REJECT]          [REJECT]
     │                  │               │                  │
     ▼                  ▼               ▼                  ▼
 Score < 8.0      Drawdown      Network Error    Insufficient
 Gate Failed      Exceeded      Connection Lost   Buying Power
 Manipulation     Max Positions  Invalid Order    Market Closed
 Detected         Reached        Parameters       Symbol Halted
```

---

## Performance Tracking Flow

### Overview
Closed trades flow through outcome tracking and performance aggregation to produce daily/monthly metrics.

```
┌─────────────────────────────────────────────────────────────┐
│              Position Closed Event                          │
│  {symbol: AAPL, entry: 150.02, exit: 150.77,               │
│   shares: 500, commission: $5, netPL: $370}                 │
└────────────────┬────────────────────────────────────────────┘
                 │
                 ▼
┌─────────────────────────────────────────────────────────────┐
│              OutcomeTracker                                 │
│          (Services/OutcomeTracker.cs)                       │
│                                                             │
│  Creates TradeOutcome record:                               │
│  ├─ Symbol: AAPL                                            │
│  ├─ Entry Time: 2025-01-31 10:00:02                        │
│  ├─ Exit Time: 2025-01-31 10:15:47                         │
│  ├─ Entry Price: $150.02                                   │
│  ├─ Exit Price: $150.77                                    │
│  ├─ Shares: 500                                             │
│  ├─ Gross P&L: $375                                        │
│  ├─ Commission: $5                                          │
│  ├─ Net P&L: $370                                           │
│  ├─ Signal Score: 8.2                                       │
│  ├─ Exit Reason: ProfitTarget                              │
│  └─ Hold Duration: 945 seconds                             │
│                                                             │
│  Saves to Database: TradeOutcomes table                     │
└────────────────┬────────────────────────────────────────────┘
                 │
                 │ TradeOutcome Saved
                 ▼
┌─────────────────────────────────────────────────────────────┐
│        PerformanceMetricsAggregator                         │
│    (Services/PerformanceMetricsAggregator.cs)               │
│                                                             │
│  Daily Aggregation (triggered at end of each trade):       │
│                                                             │
│  SELECT FROM TradeOutcomes WHERE Date = TODAY               │
│  ├─ Total Trades: 6                                        │
│  ├─ Winning Trades: 4 (NetPL > 0)                          │
│  ├─ Losing Trades: 2 (NetPL < 0)                           │
│  ├─ Gross Wins: SUM(NetPL WHERE NetPL > 0) = $850          │
│  ├─ Gross Losses: ABS(SUM(NetPL WHERE NetPL < 0)) = $300   │
│  ├─ Net P&L: SUM(NetPL) = $550                             │
│  ├─ Win Rate: (4 / 6) × 100 = 66.67%                       │
│  ├─ Profit Factor: $850 / $300 = 2.83                      │
│  ├─ Average Win: $850 / 4 = $212.50                        │
│  └─ Average Loss: $300 / 2 = $150.00                       │
│                                                             │
│  Saves to Database: DailyPerformanceSnapshots table         │
└────────────────┬────────────────────────────────────────────┘
                 │
                 ├─────────────────────┬─────────────────────┐
                 │                     │                     │
                 ▼                     ▼                     ▼
┌──────────────────────┐  ┌─────────────────┐  ┌────────────────────┐
│  NotificationService │  │ REST API        │  │ Drawdown Monitor   │
│                      │  │ /api/metrics    │  │                    │
│ Discord Webhook:     │  │                 │  │ If NetPL < -$600:  │
│ "Daily P&L: +$550    │  │ Exposes metrics │  │ → HALT TRADING     │
│  Win Rate: 66.67%    │  │ to dashboard    │  │ → Send alert       │
│  Profit Factor: 2.83"│  │                 │  │                    │
└──────────────────────┘  └─────────────────┘  └────────────────────┘
```

### Weekly and Monthly Rollups

```
┌─────────────────────────────────────────────────────────────┐
│        Weekly/Monthly Aggregation (scheduled job)           │
│                                                             │
│  SELECT FROM DailyPerformanceSnapshots                      │
│  WHERE Date BETWEEN start_of_month AND end_of_month         │
│                                                             │
│  Monthly Metrics:                                           │
│  ├─ Total Trades: SUM(TotalTrades)                          │
│  ├─ Total Net P&L: SUM(NetPL)                               │
│  ├─ Average Daily P&L: AVG(NetPL)                           │
│  ├─ Best Day: MAX(NetPL)                                    │
│  ├─ Worst Day: MIN(NetPL)                                   │
│  ├─ Trading Days: COUNT(DISTINCT Date)                      │
│  ├─ Max Drawdown: (track peak and trough)                  │
│  └─ Sharpe Ratio: (Average Return / StdDev) × √252         │
│                                                             │
│  Saves to: MonthlyPerformanceSummaries table (future)       │
└─────────────────────────────────────────────────────────────┘
```

---

## Database Schema

### Entity Relationship Diagram

```
┌─────────────────────────┐
│      TradeOutcomes       │
├─────────────────────────┤
│ Id (PK)                 │
│ Symbol                  │
│ EntryTime               │
│ ExitTime                │
│ EntryPrice              │
│ ExitPrice               │
│ Shares                  │
│ GrossPL                 │
│ NetPL                   │
│ Commission              │
│ SignalScore             │
│ ExitReason              │
│ HoldDurationSeconds     │
└────────┬────────────────┘
         │
         │ Aggregated by Date
         ▼
┌──────────────────────────────┐
│ DailyPerformanceSnapshots    │
├──────────────────────────────┤
│ Date (PK)                    │
│ TotalTrades                  │
│ WinningTrades                │
│ LosingTrades                 │
│ GrossWins                    │
│ GrossLosses                  │
│ NetPL                        │
│ ProfitFactor                 │
│ WinRate                      │
│ MaxDrawdown                  │
│ LargestWin                   │
│ LargestLoss                  │
└──────────────────────────────┘

┌─────────────────────────┐
│   ExecutionLedger        │
├─────────────────────────┤
│ Id (PK)                 │
│ OrderId                 │
│ Symbol                  │
│ Action (BUY/SELL)       │
│ OrderType               │
│ Quantity                │
│ LimitPrice              │
│ StopPrice               │
│ SubmittedAt             │
│ FilledAt                │
│ FillPrice               │
│ Commission              │
│ Status                  │
└─────────────────────────┘

┌─────────────────────────┐
│  OrderBookSnapshots      │  (Optional: historical data)
├─────────────────────────┤
│ Id (PK)                 │
│ Symbol                  │
│ Timestamp               │
│ BidLevels (JSON)        │
│ AskLevels (JSON)        │
│ BidAskRatio             │
│ Spread                  │
└─────────────────────────┘
```

### Table Details

**TradeOutcomes Table:**
- Primary record of each completed trade
- Includes both entry and exit details
- Used for backtesting and strategy refinement
- Indexed on: Symbol, EntryTime, ExitTime

**DailyPerformanceSnapshots Table:**
- Aggregated daily metrics
- Calculated at end of each trading day
- Used for trend analysis and performance reporting
- Indexed on: Date

**ExecutionLedger Table:**
- Audit trail of all order submissions
- Includes unfilled and rejected orders (not just completed trades)
- Used for execution quality analysis
- Indexed on: OrderId, Symbol, SubmittedAt

**OrderBookSnapshots Table (Future):**
- Historical Level 2 data for backtesting
- Storage-intensive (consider retention policy)
- Useful for refining signal logic offline

---

## State Transitions

### Trading System State Machine

```
┌──────────────┐
│  Startup     │
└──────┬───────┘
       │
       ▼
┌──────────────┐
│ Initializing │ (Connect to IBKR, load config)
└──────┬───────┘
       │
       ▼
┌──────────────┐
│   Standby    │ (Ready but not trading)
└──────┬───────┘
       │
       │ POST /api/admin/start-trading
       ▼
┌──────────────┐
│   Active     │ (Monitoring signals, can execute)
└──────┬───────┘
       │
       ├──────────────┬──────────────┬────────────────┐
       │              │              │                │
       ▼              ▼              ▼                ▼
┌──────────┐  ┌──────────┐  ┌──────────────┐  ┌──────────────┐
│ Position │  │  Signal  │  │Daily Drawdown│  │  Manual Stop │
│   Open   │  │Processing│  │   Exceeded   │  │              │
└──────────┘  └──────────┘  └──────┬───────┘  └──────┬───────┘
                                    │                 │
                                    │                 │
                                    ▼                 │
                             ┌──────────────┐         │
                             │ Emergency    │         │
                             │   Halt       │         │
                             └──────┬───────┘         │
                                    │                 │
                                    └─────────┬───────┘
                                              │
                                              ▼
                                       ┌──────────────┐
                                       │   Stopped    │
                                       └──────────────┘
```

### Signal Lifecycle State

```
┌──────────────┐
│   Raw Data   │ (Order book + tape updates)
└──────┬───────┘
       │
       ▼
┌──────────────┐
│   Scored     │ (OrderFlowSignalService assigns 0-10 score)
└──────┬───────┘
       │
       ▼
┌──────────────┐
│  Validating  │ (Gates check signal)
└──────┬───────┘
       │
       ├─────────────────┐
       │                 │
       ▼                 ▼
┌──────────────┐  ┌──────────────┐
│  Rejected    │  │  Validated   │ (Score ≥ 8.0, all gates passed)
└──────────────┘  └──────┬───────┘
                         │
                         ▼
                  ┌──────────────┐
                  │Risk Checking │
                  └──────┬───────┘
                         │
                         ├─────────────────┐
                         │                 │
                         ▼                 ▼
                  ┌──────────────┐  ┌──────────────┐
                  │Risk Rejected │  │  Approved    │
                  └──────────────┘  └──────┬───────┘
                                           │
                                           ▼
                                    ┌──────────────┐
                                    │   Executed   │
                                    └──────────────┘
```

---

## Related Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - System component design
- [API.md](API.md) - REST API endpoints
- [GLOSSARY.md](GLOSSARY.md) - Trading terminology
- [EXAMPLES.md](EXAMPLES.md) - Real-world flow examples
