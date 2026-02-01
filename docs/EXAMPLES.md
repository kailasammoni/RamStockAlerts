# Real-World Trading Examples

This document provides concrete examples of how RamStockAlerts detects signals, validates them, executes trades, and handles various scenarios.

---

## Table of Contents

1. [Example 1: Successful Trade (VWAP Reclaim)](#example-1-successful-trade-vwap-reclaim)
2. [Example 2: Rejected Signal (Flash Wall Detected)](#example-2-rejected-signal-flash-wall-detected)
3. [Example 3: Stop Loss Exit](#example-3-stop-loss-exit)
4. [Example 4: Dark Period Rejection](#example-4-dark-period-rejection)
5. [Example 5: Daily Drawdown Limit Hit](#example-5-daily-drawdown-limit-hit)
6. [Example 6: API Usage - Manual Trade Entry](#example-6-api-usage---manual-trade-entry)
7. [Example 7: Typical Trading Day Timeline](#example-7-typical-trading-day-timeline)
8. [Example 8: Log Output Samples](#example-8-log-output-samples)

---

## Example 1: Successful Trade (VWAP Reclaim)

### Scenario
AAPL shows strong bid pressure and reclaims VWAP at 10:15 AM ET. System scores the signal at 8.7/10 and executes a bracket order.

### Market Data Snapshot

**Time:** 2025-01-31 10:15:00 ET

**Symbol:** AAPL
**Price:** $150.00
**VWAP:** $149.75 (price just crossed above)
**Spread:** $0.02 ($149.99 bid / $150.01 ask)
**Float:** 15.5M shares
**Volume:** 2.5M shares (first 45 minutes)

**Level 2 Order Book:**
```
ASK SIDE:
$150.03 → 3,000 shares
$150.02 → 5,000 shares
$150.01 → 8,000 shares  ← Best Ask

BID SIDE:
$149.99 → 18,000 shares  ← Best Bid
$149.98 → 12,000 shares
$149.97 → 10,000 shares
$149.96 → 8,000 shares
$149.95 → 5,000 shares

Bid-Ask Ratio: 53,000 / 16,000 = 3.31 (very bullish)
```

**Time & Sales (Last 5 Trades):**
```
10:15:00 - $150.00 - 500 shares
10:14:58 - $149.99 - 800 shares
10:14:56 - $149.98 - 300 shares
10:14:54 - $149.99 - 600 shares
10:14:52 - $149.97 - 400 shares

Tape Velocity: 5 trades in 8 seconds = aggressive buying
```

### Signal Scoring

**OrderFlowSignalService Calculation:**

```json
{
  "symbol": "AAPL",
  "timestamp": "2025-01-31T10:15:00Z",
  "metrics": {
    "bidAskRatio": {
      "value": 3.31,
      "score": 2.5,
      "interpretation": "Very strong bid pressure"
    },
    "spreadCompression": {
      "previousSpread": 0.04,
      "currentSpread": 0.02,
      "compressionRate": 0.50,
      "score": 1.2,
      "interpretation": "Spread tightening, indicates accumulation"
    },
    "tapeVelocity": {
      "tradesPerSecond": 0.625,
      "aggressiveBuyRatio": 0.80,
      "score": 1.8,
      "interpretation": "Strong buying momentum"
    },
    "level2Depth": {
      "bidDepth": 53000,
      "askDepth": 16000,
      "depthRatio": 3.31,
      "score": 2.0,
      "interpretation": "Deep bid support"
    },
    "vwapReclaim": {
      "priceVsVwap": 150.00 - 149.75,
      "reclaimStrength": "strong",
      "bidPressure": 3.31,
      "score": 1.5,
      "interpretation": "Price reclaimed VWAP with strong bids (72% historical win rate)"
    }
  },
  "rawScore": 9.0
}
```

### Gate Validation

**SignalValidationEngine Pipeline:**

```json
{
  "gates": [
    {
      "name": "HardGate",
      "checks": {
        "spread": {"value": 0.02, "threshold": 0.05, "result": "PASS"},
        "price": {"value": 150.00, "range": [2, 20], "result": "PASS"},
        "float": {"value": 15500000, "range": [5000000, 50000000], "result": "PASS"}
      },
      "result": "PASS",
      "scoreAdjustment": 0.0
    },
    {
      "name": "AbsorptionReject",
      "checks": {
        "flashWallsLast10Sec": 0,
        "fakeBidWalls": false,
        "icebergSellingDetected": false
      },
      "result": "PASS",
      "scoreAdjustment": 0.0
    },
    {
      "name": "VolumeGate",
      "checks": {
        "currentVolume": 2500000,
        "minimumThreshold": 500000
      },
      "result": "PASS",
      "scoreAdjustment": 0.0
    },
    {
      "name": "TimeGate",
      "checks": {
        "currentTime": "10:15:00",
        "tradingWindow": "Primary (09:30-10:30)",
        "darkPeriod": false
      },
      "result": "PASS",
      "scoreAdjustment": 0.0
    }
  ],
  "finalScore": 9.0,
  "decision": "ACCEPTED"
}
```

### Risk Validation

**RiskManager Check:**

```json
{
  "accountSize": 40000,
  "currentPositions": 0,
  "dailyNetPL": 125.00,
  "dailyDrawdown": 0.00,
  "riskChecks": {
    "dailyDrawdownLimit": {
      "current": 0.00,
      "limit": 600.00,
      "result": "PASS"
    },
    "maxPositions": {
      "current": 0,
      "limit": 2,
      "result": "PASS"
    },
    "positionSizing": {
      "entryPrice": 150.00,
      "stopLoss": 149.75,
      "riskPerShare": 0.25,
      "accountRisk": 100.00,
      "calculatedShares": 400,
      "adjustedShares": 500,
      "result": "PASS"
    }
  },
  "decision": "APPROVED",
  "approvedShares": 500
}
```

### Order Execution

**BracketTemplateBuilder Output:**

```json
{
  "symbol": "AAPL",
  "entryOrder": {
    "orderType": "MARKET",
    "action": "BUY",
    "quantity": 500,
    "estimatedPrice": 150.01
  },
  "profitTarget": {
    "orderType": "LIMIT",
    "action": "SELL",
    "quantity": 500,
    "limitPrice": 150.75,
    "expectedProfit": 370.00
  },
  "stopLoss": {
    "orderType": "STOP",
    "action": "SELL",
    "quantity": 500,
    "stopPrice": 149.75,
    "maxLoss": -125.00
  },
  "riskRewardRatio": 2.96
}
```

**IBKR Execution Results:**

```
10:15:02 - Entry Order FILLED: BUY 500 AAPL @ $150.02 (avg fill)
10:15:02 - Profit Target SUBMITTED: SELL 500 AAPL @ $150.75 LIMIT
10:15:02 - Stop Loss SUBMITTED: SELL 500 AAPL @ $149.75 STOP

10:28:15 - Profit Target FILLED: SELL 500 AAPL @ $150.77 (avg fill)
10:28:15 - Stop Loss CANCELLED (One-Cancels-All triggered)
```

### Trade Outcome

**Final P&L:**

```json
{
  "symbol": "AAPL",
  "entryTime": "2025-01-31T10:15:02Z",
  "exitTime": "2025-01-31T10:28:15Z",
  "holdDuration": "13 minutes 13 seconds",
  "entryPrice": 150.02,
  "exitPrice": 150.77,
  "shares": 500,
  "grossPL": 375.00,
  "commissions": 5.00,
  "netPL": 370.00,
  "signalScore": 9.0,
  "exitReason": "ProfitTarget",
  "returnOnRisk": 3.70
}
```

---

## Example 2: Rejected Signal (Flash Wall Detected)

### Scenario
TSLA shows strong bid pressure at $200.00, but a large bid wall appears and disappears within 3 seconds—classic flash wall manipulation. System rejects the signal.

### Market Data Snapshot

**Time:** 2025-01-31 11:05:00 ET

**Symbol:** TSLA
**Price:** $200.00
**Spread:** $0.03

**Level 2 at 11:05:00:**
```
BID SIDE:
$200.00 → 150,000 shares  ← LARGE BID WALL APPEARS
$199.99 → 5,000 shares
$199.98 → 3,000 shares
```

**Level 2 at 11:05:03 (3 seconds later):**
```
BID SIDE:
$200.00 → 5,000 shares  ← BID WALL DISAPPEARED (not filled, just removed)
$199.99 → 5,000 shares
$199.98 → 3,000 shares
```

### Gate Validation

**AbsorptionReject Gate:**

```json
{
  "gate": "AbsorptionReject",
  "detectionTimestamp": "2025-01-31T11:05:03Z",
  "analysis": {
    "wallAppearance": {
      "timestamp": "2025-01-31T11:05:00Z",
      "price": 200.00,
      "size": 150000,
      "side": "BID"
    },
    "wallDisappearance": {
      "timestamp": "2025-01-31T11:05:03Z",
      "duration": 3.0,
      "filledShares": 0,
      "reason": "Order cancelled or hidden"
    },
    "flashWallCharacteristics": {
      "shortDuration": true,
      "noFillActivity": true,
      "disproportionateSize": true,
      "roundPriceLevel": true
    }
  },
  "decision": "HARD_REJECT",
  "reason": "Flash wall manipulation detected - bid wall appeared for 3 seconds and vanished without being filled"
}
```

### System Action

```
Signal for TSLA at 11:05:03 - REJECTED
Reason: Flash wall manipulation detected
Gate: AbsorptionReject
Raw Score: 8.3
Final Score: N/A (hard rejection)
```

**No trade executed.** Signal logged for analysis but no capital risked.

---

## Example 3: Stop Loss Exit

### Scenario
AMD signal executes at $95.00 but reverses quickly. Stop loss triggers at $94.80, limiting loss to $100.

### Trade Timeline

```
10:45:00 - Signal detected, score 8.1/10
10:45:02 - Entry FILLED: BUY 500 AMD @ $95.02
10:45:02 - Stop loss set at $94.80
10:45:02 - Profit target set at $95.60

10:47:30 - Price drops to $94.95 (minor pullback, still above stop)
10:48:15 - Price drops to $94.82 (nearing stop)
10:48:22 - Price hits $94.80
10:48:23 - Stop loss TRIGGERED
10:48:24 - Exit FILLED: SELL 500 AMD @ $94.78 (slight slippage)
```

### Trade Outcome

```json
{
  "symbol": "AMD",
  "entryTime": "2025-01-31T10:45:02Z",
  "exitTime": "2025-01-31T10:48:24Z",
  "holdDuration": "3 minutes 22 seconds",
  "entryPrice": 95.02,
  "exitPrice": 94.78,
  "shares": 500,
  "grossPL": -120.00,
  "commissions": 5.00,
  "netPL": -125.00,
  "signalScore": 8.1,
  "exitReason": "StopLoss",
  "slippage": 0.02
}
```

**Analysis:** Stop loss worked as designed. Limited loss to 0.31% of account ($125 on $40K), well within the 0.25% target (accounting for slippage).

---

## Example 4: Dark Period Rejection

### Scenario
Strong signal on NVDA at 12:30 PM, but system rejects because it's during the dark period (11:30-14:00 ET).

### Signal Details

```json
{
  "symbol": "NVDA",
  "timestamp": "2025-01-31T12:30:00Z",
  "rawScore": 8.6,
  "metrics": {
    "bidAskRatio": 3.2,
    "spreadCompression": 1.3,
    "tapeVelocity": 1.9,
    "level2Depth": 2.1,
    "vwapReclaim": 0.0
  },
  "gates": [
    {
      "name": "HardGate",
      "result": "PASS"
    },
    {
      "name": "AbsorptionReject",
      "result": "PASS"
    },
    {
      "name": "TimeGate",
      "currentTime": "12:30:00",
      "tradingWindow": "DARK_PERIOD (11:30-14:00)",
      "result": "HARD_REJECT",
      "reason": "No trading during lunchtime lull - historically low probability"
    }
  ],
  "finalDecision": "REJECTED",
  "rejectionReason": "Time-based restriction: Dark Period"
}
```

**System Action:** Signal logged but not executed. Even strong signals during low-probability hours are rejected.

---

## Example 5: Daily Drawdown Limit Hit

### Scenario
After three losing trades totaling -$650, system auto-halts for the day.

### Daily P&L Progression

```
09:45 - Trade 1: AAPL -$125 (stop loss)
10:15 - Trade 2: TSLA -$200 (stop loss)
10:45 - Trade 3: AMD -$325 (stop loss)

Total Daily Loss: -$650
Daily Drawdown Limit: $600
```

### System Response

```json
{
  "event": "DAILY_DRAWDOWN_LIMIT_EXCEEDED",
  "timestamp": "2025-01-31T10:45:24Z",
  "dailyNetPL": -650.00,
  "drawdownLimit": -600.00,
  "action": "EMERGENCY_HALT",
  "details": {
    "tradesExecuted": 3,
    "allLosses": true,
    "positionsClosed": 0,
    "tradingDisabled": true
  },
  "notifications": [
    {
      "channel": "Discord",
      "message": "⚠️ TRADING HALTED - Daily drawdown limit exceeded (-$650 / -$600 limit). No further trades today."
    }
  ],
  "nextTradingSession": "2025-02-01T09:30:00Z"
}
```

**System State:** Transitions to `STOPPED` state. Will not process any more signals until manually restarted the next trading day.

---

## Example 6: API Usage - Manual Trade Entry

### Scenario
User wants to manually override the system and enter a trade on AAPL.

### API Request

```bash
curl -X POST http://localhost:5000/api/execution/manual-entry \
  -H "Content-Type: application/json" \
  -d '{
    "symbol": "AAPL",
    "action": "BUY",
    "quantity": 500,
    "orderType": "MARKET",
    "profitTarget": 151.50,
    "stopLoss": 149.80,
    "reason": "Manual override - strong setup on longer timeframe"
  }'
```

### API Response

```json
{
  "success": true,
  "orderId": "9876543210",
  "symbol": "AAPL",
  "quantity": 500,
  "status": "Submitted",
  "timestamp": "2025-01-31T11:00:00Z",
  "estimatedRisk": 100.00,
  "bracketOrders": {
    "entry": "9876543210",
    "profitTarget": "9876543211",
    "stopLoss": "9876543212"
  },
  "riskChecks": {
    "dailyDrawdown": "PASS",
    "maxPositions": "PASS",
    "positionSize": "PASS"
  }
}
```

### Follow-up: Check Order Status

```bash
curl http://localhost:5000/api/execution/orders/9876543210
```

**Response:**

```json
{
  "orderId": "9876543210",
  "symbol": "AAPL",
  "action": "BUY",
  "quantity": 500,
  "orderType": "MARKET",
  "status": "Filled",
  "submittedAt": "2025-01-31T11:00:00Z",
  "filledAt": "2025-01-31T11:00:02Z",
  "fillPrice": 150.04,
  "commission": 2.50,
  "executionTime": "2.1s"
}
```

---

## Example 7: Typical Trading Day Timeline

### Full Day Walkthrough (2025-01-31)

```
09:20:00 - System starts, connects to IBKR TWS
09:20:05 - Database connection established
09:20:10 - STANDBY mode active, awaiting universe build

09:25:00 - Universe scanner triggered
09:25:15 - 42 symbols pass filters (price, float, spread, volume)
           Universe: AAPL, TSLA, AMD, NVDA, SPY, QQQ, ... (42 total)

09:25:20 - Subscribe to Level 2 market data for all 42 symbols
09:25:30 - Market data streams active, ready for market open

09:30:00 - MARKET OPEN
09:30:00 - Enable signal processing (Primary Window: 09:30-10:30, max 3 trades)

09:35:15 - Signal detected: AAPL score 7.8/10 → REJECTED (below 8.0 threshold)
09:42:30 - Signal detected: TSLA score 8.2/10 → VALIDATED
09:42:32 - Trade 1: BUY 500 TSLA @ $200.05
09:55:10 - Trade 1: SELL 500 TSLA @ $201.25 (Profit Target) → +$595 net

10:05:45 - Signal detected: AMD score 8.5/10 → VALIDATED
10:05:47 - Trade 2: BUY 500 AMD @ $95.00
10:18:22 - Trade 2: SELL 500 AMD @ $94.78 (Stop Loss) → -$115 net

10:25:00 - Signal detected: NVDA score 8.1/10 → VALIDATED
10:25:02 - Trade 3: BUY 500 NVDA @ $520.10
10:38:45 - Trade 3: SELL 500 NVDA @ $521.85 (Profit Target) → +$870 net

10:30:00 - Primary Window CLOSED (3 trades executed, limit reached)
10:30:00 - Enter Secondary Window (10:30-11:30, max 2 trades)

10:55:30 - Signal detected: SPY score 8.3/10 → VALIDATED
10:55:32 - Trade 4: BUY 500 SPY @ $450.00
11:15:20 - Trade 4: SELL 500 SPY @ $450.60 (Profit Target) → +$295 net

11:20:15 - Signal detected: QQQ score 7.9/10 → REJECTED (below threshold)

11:30:00 - Secondary Window CLOSED
11:30:00 - Enter DARK PERIOD (11:30-14:00, NO TRADING)

12:15:00 - Signal detected: AAPL score 8.7/10 → REJECTED (Dark Period)
12:45:30 - Signal detected: TSLA score 9.1/10 → REJECTED (Dark Period)

14:00:00 - Dark Period ENDED
14:00:00 - Enter Final Window (14:00-15:00, max 2 trades, conditional)

14:25:10 - Signal detected: AAPL score 8.0/10 → VALIDATED (barely)
14:25:12 - Trade 5: BUY 500 AAPL @ $150.00
14:40:55 - Trade 5: SELL 500 AAPL @ $150.50 (Profit Target) → +$245 net

15:00:00 - Final Window CLOSED
15:00:00 - Market winding down, signal processing disabled

15:55:00 - Close all remaining positions (if any) - NONE OPEN
16:00:00 - MARKET CLOSE

16:05:00 - Daily Performance Aggregation
           Total Trades: 5
           Winning Trades: 4
           Losing Trades: 1
           Gross Wins: $2,005
           Gross Losses: $115
           Net P&L: $1,890
           Profit Factor: 17.43
           Win Rate: 80%

16:10:00 - Discord Notification Sent:
           "Daily Summary - Jan 31, 2025
            Trades: 5 (4W / 1L)
            Net P&L: +$1,890
            Profit Factor: 17.43
            Win Rate: 80%"

16:15:00 - System enters STANDBY mode, awaiting next trading day
```

---

## Example 8: Log Output Samples

### Startup Logs

```
[2025-01-31 09:20:00] INFO  - RamStockAlerts starting...
[2025-01-31 09:20:01] INFO  - Mode: LIVE
[2025-01-31 09:20:02] INFO  - Database connection established (SQL Server)
[2025-01-31 09:20:03] INFO  - Connecting to IBKR TWS at 127.0.0.1:7497
[2025-01-31 09:20:05] INFO  - IBKR connection successful (Client ID: 1)
[2025-01-31 09:20:05] INFO  - System ready, entering STANDBY mode
```

### Universe Building Logs

```
[2025-01-31 09:25:00] INFO  - Starting universe scan...
[2025-01-31 09:25:02] INFO  - IBKR Scanner: Retrieved 150 symbols
[2025-01-31 09:25:05] INFO  - Filtering symbols (price: $2-$20, float: 5M-50M, spread: $0.02-$0.05)
[2025-01-31 09:25:10] INFO  - Universe Filter: 42 symbols passed
[2025-01-31 09:25:15] INFO  - Subscribing to market data for 42 symbols
[2025-01-31 09:25:20] INFO  - Market data subscriptions active
[2025-01-31 09:25:30] INFO  - DepthDeltaTracker initialized for 42 symbols
```

### Signal Processing Logs

```
[2025-01-31 09:42:30] INFO  - Signal detected: TSLA
[2025-01-31 09:42:30] DEBUG - OrderFlowSignalService: TSLA raw score = 8.2
[2025-01-31 09:42:30] DEBUG - SignalValidationEngine: Running gates...
[2025-01-31 09:42:30] DEBUG -   HardGate: PASS
[2025-01-31 09:42:30] DEBUG -   AbsorptionReject: PASS (no flash walls)
[2025-01-31 09:42:30] DEBUG -   TimeGate: PASS (Primary Window)
[2025-01-31 09:42:30] DEBUG -   VolumeGate: PASS
[2025-01-31 09:42:30] INFO  - Signal validated: TSLA final score = 8.2
[2025-01-31 09:42:30] INFO  - RiskManager: Approving 500 shares
[2025-01-31 09:42:31] INFO  - ExecutionService: Submitting bracket order for TSLA
[2025-01-31 09:42:32] INFO  - IBKR: Order 1234567890 FILLED - BUY 500 TSLA @ $200.05
[2025-01-31 09:42:32] INFO  - Position opened: TSLA 500 shares @ $200.05
```

### Trade Exit Logs

```
[2025-01-31 09:55:10] INFO  - IBKR: Order 1234567891 FILLED - SELL 500 TSLA @ $201.25 (Profit Target)
[2025-01-31 09:55:10] INFO  - Position closed: TSLA
[2025-01-31 09:55:10] INFO  - OutcomeTracker: Recording trade outcome
[2025-01-31 09:55:11] INFO  - Trade completed:
                             Symbol: TSLA
                             Entry: $200.05 @ 09:42:32
                             Exit: $201.25 @ 09:55:10
                             Shares: 500
                             Gross P&L: $600.00
                             Commission: $5.00
                             Net P&L: $595.00
                             Hold: 12m 38s
                             Exit Reason: ProfitTarget
```

### Rejection Logs

```
[2025-01-31 12:15:00] INFO  - Signal detected: AAPL
[2025-01-31 12:15:00] DEBUG - OrderFlowSignalService: AAPL raw score = 8.7
[2025-01-31 12:15:00] DEBUG - SignalValidationEngine: Running gates...
[2025-01-31 12:15:00] DEBUG -   TimeGate: REJECT (Dark Period 11:30-14:00)
[2025-01-31 12:15:00] WARN  - Signal rejected: AAPL - Dark Period restriction
```

### Daily Summary Logs

```
[2025-01-31 16:05:00] INFO  - Aggregating daily performance...
[2025-01-31 16:05:01] INFO  - Daily Summary for 2025-01-31:
                             Total Trades: 5
                             Winning Trades: 4
                             Losing Trades: 1
                             Win Rate: 80.00%
                             Gross Wins: $2,005.00
                             Gross Losses: $115.00
                             Net P&L: $1,890.00
                             Profit Factor: 17.43
                             Commissions: $25.00
[2025-01-31 16:05:02] INFO  - Saving DailyPerformanceSnapshot to database
[2025-01-31 16:10:00] INFO  - Discord notification sent
```

---

## Related Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - System design and components
- [API.md](API.md) - REST API endpoint reference
- [GLOSSARY.md](GLOSSARY.md) - Trading terminology definitions
- [DATA_FLOW.md](DATA_FLOW.md) - Data flow diagrams and database schema
- [README.md](../README.md) - Project overview and quick start
