# System Architecture

## Overview

RamStockAlerts is a multi-layered .NET application for algorithmic stock trading. The system ingests real-time market data from Interactive Brokers, validates trading signals through a sophisticated gate system, executes bracket orders, and tracks performance metrics.

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        External Systems                          │
├─────────────────────────────────────────────────────────────────┤
│  IBKR TWS (Level 2)  │  Polygon.io  │  Alpaca API  │  Discord   │
└──────────┬──────────────────┬───────────────┬──────────┬────────┘
           │                  │               │          │
┌──────────▼──────────────────▼───────────────▼──────────▼────────┐
│                      RamStockAlerts API                          │
│                      (ASP.NET Core)                              │
├─────────────────────────────────────────────────────────────────┤
│  Controllers Layer                                               │
│  ├── AdminController (system control)                           │
│  ├── ExecutionController (manual trades)                        │
│  ├── IBKRScannerController (universe scanning)                  │
│  └── PerformanceMetricsController (metrics)                     │
├─────────────────────────────────────────────────────────────────┤
│  Services Layer                                                  │
│  ├── OrderFlowSignalService (scoring 0-10)                      │
│  ├── DepthDeltaTracker (Level 2 monitoring)                     │
│  ├── DepthUniverseFilter (symbol filtering)                     │
│  ├── OutcomeTracker (trade result logging)                      │
│  ├── PerformanceMetricsAggregator (daily rollup)                │
│  └── NotificationService (Discord alerts)                       │
├─────────────────────────────────────────────────────────────────┤
│  Engine Layer                                                    │
│  ├── SignalValidationEngine (orchestrates gates)                │
│  ├── GateTrace (validation pipeline)                            │
│  ├── AbsorptionReject (fake wall detection)                     │
│  └── HardGate (hard rejection rules)                            │
├─────────────────────────────────────────────────────────────────┤
│  Feeds Layer                                                     │
│  └── IBKRClient (TWS API wrapper)                               │
└─────────────────────────────────────────────────────────────────┘
           │                                           │
┌──────────▼───────────────────────────────────────────▼──────────┐
│             RamStockAlerts.Execution Module                      │
│             (Separate Project)                                   │
├─────────────────────────────────────────────────────────────────┤
│  ├── ExecutionService (order submission)                        │
│  ├── RiskManager (position sizing, risk checks)                 │
│  ├── OrderStateTracker (order lifecycle)                        │
│  ├── BracketTemplateBuilder (profit + stop orders)              │
│  └── ExecutionLedger (trade history)                            │
└─────────────────────────────────────────────────────────────────┘
           │
┌──────────▼──────────────────────────────────────────────────────┐
│                   Data Persistence Layer                         │
│                   (Entity Framework Core)                        │
├─────────────────────────────────────────────────────────────────┤
│  Database: SQL Server / PostgreSQL                              │
│  ├── TradeOutcomes (completed trades with P&L)                  │
│  ├── DailyPerformanceSnapshots (daily metrics)                  │
│  ├── OrderBookSnapshots (Level 2 historical data)               │
│  └── ExecutionLedger (order execution history)                  │
└─────────────────────────────────────────────────────────────────┘
```

## Component Deep Dive

### 1. Controllers Layer (API Endpoints)

**AdminController** [`src/RamStockAlerts/Controllers/AdminController.cs`](../src/RamStockAlerts/Controllers/AdminController.cs)
- System health checks
- Trading enable/disable controls
- Configuration management
- Emergency shutdown procedures

**ExecutionController** [`src/RamStockAlerts/Controllers/ExecutionController.cs`](../src/RamStockAlerts/Controllers/ExecutionController.cs)
- Manual trade entry (override automation)
- Position query (current open positions)
- Position closing (force exit)
- Order status lookup

**IBKRScannerController** [`src/RamStockAlerts/Controllers/IBKRScannerController.cs`](../src/RamStockAlerts/Controllers/IBKRScannerController.cs)
- Symbol universe scanning
- Pre-market universe building (09:25 ET)
- Real-time symbol filtering
- Float/volume/spread validation

**PerformanceMetricsController** [`src/RamStockAlerts/Controllers/PerformanceMetricsController.cs`](../src/RamStockAlerts/Controllers/PerformanceMetricsController.cs)
- Daily P&L summary
- Win rate calculations
- Profit factor tracking
- Historical performance queries

---

### 2. Services Layer (Business Logic)

**OrderFlowSignalService** [`src/RamStockAlerts/Services/OrderFlowSignalService.cs`](../src/RamStockAlerts/Services/OrderFlowSignalService.cs)
- **Purpose:** Scores trading opportunities on a 0-10 scale
- **Inputs:** Order book snapshots, tape data, VWAP
- **Outputs:** Signal score + breakdown of contributing factors
- **Key Metrics:**
  - Bid-Ask Ratio (bid pressure / ask pressure)
  - Spread Compression (spread tightening rate)
  - Tape Velocity (trades per second)
  - Level 2 Depth (cumulative bid vs ask size)
  - VWAP Reclaim (price crossing above VWAP)

**DepthDeltaTracker** [`src/RamStockAlerts/Services/DepthDeltaTracker.cs`](../src/RamStockAlerts/Services/DepthDeltaTracker.cs)
- **Purpose:** Monitors Level 2 order book changes in real-time
- **Functionality:**
  - Tracks bid/ask wall appearance and disappearance
  - Detects flash walls (orders that disappear within 2-5 seconds)
  - Calculates bid-ask ratio over rolling time windows
  - Identifies iceberg orders (hidden large orders)

**DepthUniverseFilter** [`src/RamStockAlerts/Services/DepthUniverseFilter.cs`](../src/RamStockAlerts/Services/DepthUniverseFilter.cs)
- **Purpose:** Filters trading universe in real-time
- **Criteria:**
  - Price range: $2-$20
  - Float: 5M-50M shares
  - Spread: $0.02-$0.05
  - Minimum daily volume threshold
  - Liquidity requirements

**OutcomeTracker** [`src/RamStockAlerts/Services/OutcomeTracker.cs`](../src/RamStockAlerts/Services/OutcomeTracker.cs)
- **Purpose:** Logs completed trades with P&L
- **Records:**
  - Entry/exit prices and times
  - Gross/net P&L (including commissions)
  - Hold duration
  - Signal score at entry
  - Exit reason (profit target, stop loss, time exit)

**PerformanceMetricsAggregator** [`src/RamStockAlerts/Services/PerformanceMetricsAggregator.cs`](../src/RamStockAlerts/Services/PerformanceMetricsAggregator.cs)
- **Purpose:** Aggregates daily/weekly/monthly performance
- **Metrics Calculated:**
  - Total trades, wins, losses
  - Profit factor (gross wins / gross losses)
  - Win rate percentage
  - Average win vs average loss
  - Maximum drawdown
  - Sharpe ratio

**NotificationService** [`src/RamStockAlerts/Services/NotificationService.cs`](../src/RamStockAlerts/Services/NotificationService.cs)
- **Purpose:** Sends alerts to Discord
- **Notifications:**
  - High-probability signals (8.0+ score)
  - Trade executions
  - Daily P&L summaries
  - System errors and warnings

---

### 3. Engine Layer (Signal Validation)

**SignalValidationEngine** [`src/RamStockAlerts/Engine/SignalValidationEngine.cs`](../src/RamStockAlerts/Engine/SignalValidationEngine.cs)
- **Purpose:** Orchestrates all validation gates
- **Process:**
  1. Receives raw signal from OrderFlowSignalService
  2. Runs signal through 8+ validation gates
  3. Collects gate results (pass, reduce score, hard reject)
  4. Applies score adjustments
  5. Returns final validated signal or rejection

**GateTrace** [`src/RamStockAlerts/Engine/GateTrace.cs`](../src/RamStockAlerts/Engine/GateTrace.cs)
- **Purpose:** Maintains audit trail of gate decisions
- **Functionality:**
  - Logs which gates passed/failed
  - Records score adjustments
  - Tracks rejection reasons
  - Used for signal validation debugging

**AbsorptionReject** [`src/RamStockAlerts/Engine/AbsorptionReject.cs`](../src/RamStockAlerts/Engine/AbsorptionReject.cs)
- **Purpose:** Detects fake absorption (bid wall manipulation)
- **Rejection Criteria:**
  - Large bid wall appears and disappears within 5 seconds
  - Bid wall size disproportionate to recent volume
  - Wall appears at round price levels ($10.00, $15.00)

**HardGate** [`src/RamStockAlerts/Engine/HardGate.cs`](../src/RamStockAlerts/Engine/HardGate.cs)
- **Purpose:** Instant rejection rules
- **Examples:**
  - Spread > $0.05 (too wide for 400-600 share position)
  - Price outside $2-$20 range
  - Float outside 5M-50M shares
  - Volume < minimum threshold

---

### 4. Feeds Layer (Market Data)

**IBKRClient** [`src/RamStockAlerts/Feeds/IBKRClient.cs`](../src/RamStockAlerts/Feeds/IBKRClient.cs)
- **Purpose:** Wrapper around IBKR TWS API
- **Data Streams:**
  - Level 2 Market Depth (order book)
  - Time & Sales (tape data)
  - Last price, bid, ask
  - Volume and VWAP
- **Connection:**
  - Connects to TWS on localhost:7497 (or IB Gateway 4001)
  - Maintains persistent connection with reconnection logic
  - Handles market data subscriptions per symbol

---

### 5. Execution Module (Separate Project)

**ExecutionService** [`src/RamStockAlerts.Execution/Services/ExecutionService.cs`](../src/RamStockAlerts.Execution/Services/ExecutionService.cs)
- **Purpose:** Submits and manages orders
- **Functionality:**
  - Converts signals to IBKR orders
  - Submits bracket orders (entry + target + stop)
  - Monitors order status updates
  - Handles partial fills and rejections

**RiskManager** [`src/RamStockAlerts.Execution/Risk/RiskManager.cs`](../src/RamStockAlerts.Execution/Risk/RiskManager.cs)
- **Purpose:** Position sizing and risk validation
- **Checks:**
  - Max position size: 400-600 shares
  - Risk per trade: ≤ 0.25% of account
  - Daily drawdown: < $600
  - Maximum concurrent positions: 1-2
  - Account size limits ($30K-$80K range)

**OrderStateTracker** [`src/RamStockAlerts.Execution/Services/OrderStateTracker.cs`](../src/RamStockAlerts.Execution/Services/OrderStateTracker.cs)
- **Purpose:** Tracks order lifecycle
- **States:**
  - Submitted → PendingSubmit → PreSubmitted
  - Filled (complete or partial)
  - Cancelled
  - Rejected (with reason)

**BracketTemplateBuilder** [`src/RamStockAlerts.Execution/Services/BracketTemplateBuilder.cs`](../src/RamStockAlerts.Execution/Services/BracketTemplateBuilder.cs)
- **Purpose:** Constructs bracket order sets
- **Output:**
  - Parent order: Market or limit entry
  - Profit target: Limit order at calculated target
  - Stop loss: Stop order at calculated stop

**ExecutionLedger** [`src/RamStockAlerts.Execution/Storage/ExecutionLedger.cs`](../src/RamStockAlerts.Execution/Storage/ExecutionLedger.cs)
- **Purpose:** Persistent storage of execution history
- **Records:**
  - Order submissions with timestamps
  - Fill confirmations with execution prices
  - Commission costs
  - Slippage calculations

---

## Data Flow

### Signal Generation to Execution Flow

```
1. Market Data Ingestion
   IBKRClient subscribes to Level 2 depth for filtered symbols
   │
   ▼
2. Depth Monitoring
   DepthDeltaTracker detects order book changes
   │
   ▼
3. Universe Filtering
   DepthUniverseFilter ensures symbol still meets criteria
   │
   ▼
4. Signal Scoring
   OrderFlowSignalService calculates 0-10 score
   │
   ▼
5. Signal Validation
   SignalValidationEngine runs through 8+ gates
   │
   ├─ AbsorptionReject (check for fake walls)
   ├─ HardGate (instant rejection rules)
   ├─ ... (6+ additional gates)
   │
   ▼
6. Score Threshold Check
   If score < 8.0 → Reject
   If score ≥ 8.0 → Proceed
   │
   ▼
7. Risk Validation
   RiskManager checks position size, daily drawdown, account limits
   │
   ▼
8. Order Construction
   BracketTemplateBuilder creates entry + target + stop orders
   │
   ▼
9. Order Submission
   ExecutionService submits to IBKR
   │
   ▼
10. Order Monitoring
    OrderStateTracker monitors fill status
    │
    ▼
11. Outcome Recording
    OutcomeTracker logs P&L when position closes
    │
    ▼
12. Performance Aggregation
    PerformanceMetricsAggregator updates daily metrics
```

---

## Database Schema (Entity Framework Core)

### TradeOutcomes Table
```sql
CREATE TABLE TradeOutcomes (
    Id INT PRIMARY KEY,
    Symbol VARCHAR(10) NOT NULL,
    EntryTime DATETIME NOT NULL,
    ExitTime DATETIME,
    EntryPrice DECIMAL(10,4),
    ExitPrice DECIMAL(10,4),
    Shares INT,
    GrossPL DECIMAL(10,2),
    NetPL DECIMAL(10,2),  -- After commissions
    Commission DECIMAL(6,2),
    SignalScore DECIMAL(3,1),
    ExitReason VARCHAR(50),  -- ProfitTarget, StopLoss, TimeExit, Manual
    HoldDurationSeconds INT
)
```

### DailyPerformanceSnapshots Table
```sql
CREATE TABLE DailyPerformanceSnapshots (
    Date DATE PRIMARY KEY,
    TotalTrades INT,
    WinningTrades INT,
    LosingTrades INT,
    GrossWins DECIMAL(10,2),
    GrossLosses DECIMAL(10,2),
    NetPL DECIMAL(10,2),
    ProfitFactor DECIMAL(5,2),
    WinRate DECIMAL(5,2),
    MaxDrawdown DECIMAL(10,2),
    LargestWin DECIMAL(10,2),
    LargestLoss DECIMAL(10,2)
)
```

### ExecutionLedger Table
```sql
CREATE TABLE ExecutionLedger (
    Id INT PRIMARY KEY,
    OrderId VARCHAR(50) NOT NULL,
    Symbol VARCHAR(10),
    Action VARCHAR(10),  -- BUY, SELL
    OrderType VARCHAR(20),  -- MARKET, LIMIT, STOP
    Quantity INT,
    LimitPrice DECIMAL(10,4),
    StopPrice DECIMAL(10,4),
    SubmittedAt DATETIME,
    FilledAt DATETIME,
    FillPrice DECIMAL(10,4),
    Commission DECIMAL(6,2),
    Status VARCHAR(20)  -- Submitted, Filled, Cancelled, Rejected
)
```

---

## Configuration and Environment

### Mode Selection
The system operates in three modes (set via `MODE` environment variable):

**1. Live Mode** (`MODE=live`)
- Executes real trades with real money
- Requires IBKR live account
- Enables all risk management checks

**2. Shadow Mode** (`MODE=shadow`)
- Observes and scores signals
- Does NOT execute trades
- Logs hypothetical trades for backtesting
- Safe for production validation

**3. Replay Mode** (`MODE=replay`, `SYMBOL=AAPL`)
- Replays historical data for specific symbol
- Used for backtesting and strategy refinement
- Runs faster than real-time

### Key Configuration Files
- `appsettings.json` - Production configuration
- `appsettings.Development.json` - Development overrides
- `appsettings.Testing.json` - Test environment settings

---

## Testing Architecture

### Unit Tests (`RamStockAlerts.Tests`)
- **Signal Validation Tests:** AbsorptionRejectTests, HardGateTests
- **Service Tests:** DepthDeltaTrackerTests, OrderFlowSignalServiceTests
- **Metrics Tests:** PerformanceMetricsAggregatorTests
- **Execution Tests:** ExecutionControllerTests

### Integration Tests
- **Full Pipeline Tests:** DailyRollupFullIntegrationTests
- **Database Tests:** Use in-memory EF Core provider
- **IBKR Mock Tests:** Simulate TWS responses

### Test Data Fixtures
- Realistic order book snapshots
- Historical trade data
- Pre-calculated signal scores for validation

---

## Security Considerations

### Secrets Management
- NEVER commit API keys to source control
- Use environment variables or Azure Key Vault
- Rotate Polygon and Alpaca keys regularly
- IBKR credentials via TWS login (not stored in code)

### API Authentication
- Admin endpoints require authentication
- Execution endpoints require elevated permissions
- Metrics endpoints read-only

### Rate Limiting
- IBKR API: 50 requests/second limit
- Polygon API: Tier-based limits
- Internal throttling to prevent API bans

---

## Performance Characteristics

### Latency Requirements
- Signal generation: < 100ms
- Order submission: < 500ms end-to-end
- Market data processing: < 50ms per update

### Scalability
- Designed for 20-50 concurrent symbol subscriptions
- Single-instance deployment (no horizontal scaling needed)
- Database optimized for 10K+ trades/month

### Resource Usage
- Memory: ~500MB typical, 1GB peak (during market hours)
- CPU: < 10% average, 30% peak (signal validation)
- Network: ~5 Mbps (market data streams)

---

## Deployment Architecture

### Recommended Setup
```
┌─────────────────────────────────────────┐
│  Windows Server / Ubuntu Server         │
│  ├── RamStockAlerts (ASP.NET Core)      │
│  ├── IBKR TWS / IB Gateway              │
│  ├── SQL Server / PostgreSQL            │
│  └── Monitoring (Grafana, Prometheus)   │
└─────────────────────────────────────────┘
```

### Alternative: Docker Deployment
```
docker-compose.yml:
  - ramstockalerts (ASP.NET Core container)
  - postgres (database)
  - grafana (monitoring)
```

---

## Monitoring and Observability

### Key Metrics to Monitor
- Signal generation rate (signals/minute)
- Signal score distribution (how many 8.0+?)
- Order fill rate (% of submitted orders filled)
- Daily P&L tracking
- System uptime during market hours
- API error rates (IBKR, Polygon)

### Alerting Thresholds
- Daily drawdown > $600 → Emergency shutdown
- Fill rate < 90% → Investigate order rejections
- Signal rate < 5/day → Check market data feed
- API errors > 5% → Check IBKR connection

---

## Future Architecture Enhancements

### Potential Additions
- **Machine Learning Layer:** Train signal scoring model on historical outcomes
- **Multi-Broker Support:** Add TD Ameritrade, Charles Schwab APIs
- **Cloud Deployment:** Azure App Service or AWS ECS
- **Real-time Dashboard:** WebSocket-based live metrics UI
- **Advanced Risk Models:** Portfolio-level VaR, correlation analysis

---

## Related Documentation
- [API.md](API.md) - REST endpoint reference
- [GLOSSARY.md](GLOSSARY.md) - Trading terminology
- [DATA_FLOW.md](DATA_FLOW.md) - Detailed data flow diagrams
- [EXAMPLES.md](EXAMPLES.md) - Real-world trading scenarios
- [ProductGoalV2.md](../ProductGoalV2.md) - Strategic goals and specifications
