# RamStockAlerts – Order-Flow Intelligence Platform

An **order-flow intelligence platform** designed to extract a small, repeatable daily edge from liquidity dislocations.

## System Objective

Detect **transient order-book imbalances** that statistically precede short-term price continuation and deliver **actionable, human-executed trade blueprints** with strict quality control.

---

# Functional Requirements

## 1. Dynamic Trading Universe

| Feature          | Requirement                                          |
| ---------------- | ---------------------------------------------------- |
| Universe Builder | Auto-build daily symbol universe every 5 minutes     |
| Filters          | Price 2–50, RelVol > 2, Spread < 0.05, Float < 50M |
| Exclusions       | ETFs, ADRs, OTC, halted symbols                      |
| Persistence      | Store active universe in Redis with TTL              |

---

## 2. Liquidity Metrics Engine

Must compute in real time:

| Metric             | Definition                            |
| ------------------ | ------------------------------------- |
| Spread Compression | (Ask − Bid) delta over last 3s        |
| Bid-Ask Ratio      | ΣBidSize / ΣAskSize                   |
| Tape Velocity      | Trades per second (rolling 3s)        |
| VWAP Reclaim       | Price crossing VWAP with volume surge |
| Book Stability     | Bid wall persistence ≥ 1000ms         |

---

## 3. Signal Scoring System

| Rule              | Weight |
| ----------------- | ------ |
| Spread ≤ 0.03     | 2      |
| Bid-Ask Ratio ≥ 3 | 3      |
| Tape Speed ≥ 5    | 2      |
| VWAP reclaim      | 2      |
| Bid > Ask Size    | 1      |
| **Max Score**     | **10** |

Alert only if **Score ≥ 7.5**.

---

## 4. False Signal Rejection

| Pattern                          | Action |
| -------------------------------- | ------ |
| Fake bid walls                   | Reject |
| Ask replenishment speed > fills  | Reject |
| Spread widens > 50% post-trigger | Cancel |
| Tape slowdown                    | Cancel |

---

## 5. Signal Throttling

| Rule                 | Effect       |
| -------------------- | ------------ |
| Same ticker < 10 min | Suppress     |
| >3 alerts/hour       | Engine pause |
| News / halt detected | Global halt  |

---

## 6. Trade Blueprint Generator

| Field         | Rule                         |
| ------------- | ---------------------------- |
| Entry         | Last Ask                     |
| Stop          | Entry − (Spread × 4)         |
| Target        | Entry + (Spread × 8)         |
| Position Size | Risk capped at 0.25% account |

---

## 7. Notification Engine

| Feature     | Requirement        |
| ----------- | ------------------ |
| Transport   | Discord webhook    |
| Format      | Rich embeds        |
| SLA         | <1 second delivery |
| Reliability | Retry + backoff    |

---

## 8. Performance Tracking

| Metric                  | Stored |
| ----------------------- | ------ |
| Win Rate                | Yes    |
| Avg Gain/Loss           | Yes    |
| Signal Score vs Outcome | Yes    |
| Time-of-Day Edge        | Yes    |

---

## 9. Safety Systems

| Risk                   | Mitigation        |
| ---------------------- | ----------------- |
| Liquidity collapse     | Auto cancel       |
| Polygon feed lag       | Engine suspend    |
| Alert spam             | Hard rate limits  |
| Emotional over-trading | 3 alerts/hour cap |

---

## 10. Daily Operational Checklist

| Step        | Action              |
| ----------- | ------------------- |
| 09:25       | Universe build      |
| 09:30–11:30 | Signal window       |
| 12:00–14:00 | Low-confidence only |
| 15:45       | Shutdown engine     |

---

# Non-Functional Requirements

| Area          | Requirement               |
| ------------- | ------------------------- |
| Latency       | <500ms ingestion-to-alert |
| Uptime        | 99.9%                     |
| Determinism   | Replayable sessions       |
| Observability | Full event logs           |
| Scalability   | 500 symbols concurrently  |

---

# Success Criteria

| Metric       | Target                |
| ------------ | --------------------- |
| Avg Win      | ≥ 0.45%               |
| Avg Loss     | ≤ −0.30%              |
| Accuracy     | ≥ 62%                 |
| Max Drawdown | ≤ 1.5% daily          |
| Trades/Day   | 6-9 high-quality only |

---

# Core Principles

**This system does not predict price.**

It detects **liquidity failure points** — moments where institutions cannot hide their intent. That is where small, repeatable profits live.

**Scarcity over volume:** Max 6-9 high-quality trades/day. Detect dislocations, not trends. Use book + tape as primary signal drivers.

---

# Implementation Details

## Trade Signal Criteria (Quantitative Score 0–10)
- Accept if score ≥ 7.5
- Components:
  - Queue Imbalance (QI)
  - Absorption (fill vs cancel at ask)
  - Tape speed (volume/time accel)
  - Wall pressure and depletion
  - VWAP reclaim bonus (+0.5)
  - Spread penalty

## Post-Signal Filters
- **Spoofing rejection:** large cancel near ask
- **Replenishment rejection:** refill after fill
- **Tape stall:** no recent prints
- **(Planned):** spread blowout, tape freeze filter

---

## Blueprint Generation
- Entry = Ask
- Stop = Entry − 4×Spread
- Target = Entry + 8×Spread
- Stored per signal in trade journal

## Universe Construction
- IBKR Scanner live
  - Filters: Price $5–20, Float < 150M, common stocks only
  - Excludes: ETFs, ADRs, warrants, OTC
- (Planned): enforce rel vol > 2, spread < $0.05

## Journal & Replay
- Every signal captured (accepted/rejected)
- JSONL format with timestamp, ticker, score, rejection reason
- Used for: strategy audit, replay testing, outcome tracking

## Performance Tracking (WIP)
- Signals journaled but not yet scored as wins/losses
- Outcome tagging (TP/hit/SL/miss) needed
- Profit factor, drawdown, hit rate → not implemented yet

---

# RamStockAlerts – Small Account Edge System

**A capacity-constrained scalping system designed to extract $700–$1,000/week from institutional liquidity deserts using IBKR native streaming.**

---

## System Objective

Exploit micro-inefficiencies in sub-institutional liquidity ($30K–$50K account scale) where HFTs cannot operate profitably due to position size constraints, and retail traders lack order-flow visualization.

**Primary Goal**: Generate $700–$1,000 gross profit per 5-day trading week (1.4–2% weekly return) on a $40K account.  
**Secondary Goal**: Maintain sub-$100K account ceiling to preserve execution edge.

---

## Core Constraints (Hard Limits)

- **Account Size**: $30K–$80K. Above $100K, you become the liquidity event you're hunting
- **Position Size**: 400–600 shares max. Prevents Level 2 exhaustion (avoid moving the ask)
- **Weekly Target**: $700–$1,000. 2.5 winning trades/day avg; stops greed-driven overtrading  
- **Symbol Universe**: 100–150 max. IBKR pacing limits (100 msg/sec socket constraint)
- **Max Drawdown**: $600/day (1.5%). PDT protection buffer + psychological circuit breaker
- **Risk/Trade**: 0.25% ($75–$100). Survives 6 consecutive losses without ruin

---

## 1. Trading Universe (Realistic)

**Data Source**: IBKR Socket Client (TWS API) – Native Level I/II, not Polygon.

### Filters
- **Price**: $2.00–$20.00. Avoids hard-to-borrow fees above $20; avoids delisting risk below $2
- **Float**: 5M–50M shares. Sweet spot: liquid enough to fill, small enough to move
- **Spread**: $0.02–$0.05. Acceptable slippage cost; >$0.05 = too wide for scalping
- **Rel Volume**: >2.0 vs 20-day avg. Ensures tape velocity signal validity
- **Exchange**: NASDAQ/NYSE only. Avoid OTC halts; better regulatory oversight

### Exclusions
ETFs, ADRs, Warrants, Biotech (binary events). Prevents 10% gap-down overnight risk.

### Infrastructure Reality
- Run 2x TWS/IB Gateway instances (50 symbols each) to avoid pacing violations
- Redis cache for active universe with 5-min TTL refresh
- **No more than 150 symbols** – IBKR will throttle socket messages beyond this

---

## 2. Liquidity Metrics (IBKR Native)

Calculated on `tickSize()` / `tickPrice()` callbacks (not REST polling).

**Bid-Ask Ratio**: Level 2 bid size / ask size (top 3 levels) → Threshold ≥2.5:1  
**Spread Compression**: (Ask – Bid) sustained <0.04 for 2+ seconds → Threshold ≤$0.04  
**Tape Velocity**: Trades/sec via `tickString()` last trade → Threshold ≥4 trades/sec  
**Level 2 Depth**: Shares available at ask → Threshold ≥1,200 shares  
**VWAP Delta**: Price vs IBKR calculated VWAP → Within ±$0.02  

**Critical Filter**: If Level 2 depth <1,200 shares at ask, **suppress alert** – you'll slip into the next level and ruin the 2:1 R/R math.

---

## 3. Signal Scoring (Revised Weights)

- **Bid-Ask Ratio ≥2.5**: 3 points. Primary edge in small-caps (institutional absorption)
- **Spread ≤$0.04**: 2 points. Commission drag control
- **Tape Velocity ≥4/sec**: 2 points. Confirms active participation (not spoofing)
- **Level 2 Depth ≥1,200**: 2 points. **New**: Ensures fill quality
- **VWAP reclaim (price crossing up)**: 1 point. Momentum confirmation

**Trigger Score**: ≥8.0/10 (raised from 7.5 to compensate for smaller size)

---

## 4. False Signal Rejection (Small-Cap Specific)

Small-cap manipulation is higher than large-cap; spoofing is cheaper here.

**Flash Bid Wall**: >5,000 shares appears at best bid, cancels within 500ms  
→ Action: Reject (HFT inventory games)

**Ask Replenishment**: Ask size refills faster than tape prints  
→ Action: Reject (supply overwhelming demand)

**Spread Blowout**: Spread widens >50% within 2s of trigger  
→ Action: Cancel (liquidity evaporating)

**Halt Risk**: Price moves >3% in 60 seconds  
→ Action: Global halt (avoid volatility halt trap)

---

## 5. Trade Blueprint (Realistic Execution)

Example parameters for a $12 stock with $0.03 spread:

**Entry**: Ask + $0.01 (slippage buffer) = $12.04  
**Stop**: Entry – (Spread × 3) = $11.95 (-$0.09)  
**Target**: Entry + (Spread × 6) = $12.22 (+$0.18)  
**Shares**: Risk $100 ÷ $0.09 = 1,111 → **Cap at 500** (account for slippage)  
**Position Risk**: $45 (0.11% of $40K). Conservative due to gap risk  
**Expected Net**: (500 × $0.18) – $3.50 commissions = **$86.50 profit**

*Why 2:1 R/R changed to 3:6*: Commissions ($3.50 round-trip on 500 shares) eat less of the profit; allows for partial fills.

---

## 6. Signal Throttling (Account Protection)

- **Same ticker < 15 min**: Suppress (avoid churn)
- **>2 losses in 30 min**: Session pause (emotional override)
- **Daily P&L < -$400**: Hard stop (preserve capital for next day)
- **>3 signals/hour**: Throttle to 1 every 20 min (quality control)

---

## 7. Validation Protocol (Before Live)

### Phase 1: Shadow Trading (Week 1–2)
- 100-share lots only ($10 risk/trade)
- Log theoretical fills vs actual Level 2 prints
- Target: 60%+ win rate, breakeven after commissions

### Phase 2: Micro Size (Week 3–4)
- 200–300 shares
- Target: $200/week (prove edge exists)

### Phase 3: Full Size (Week 5+)
- 400–500 shares
- Target: $700–1,000/week

**Abort Condition**: If Phase 1 shows <55% win rate after 50 signals, system has no edge – do not scale.

---

## 8. Heatmap Integration (Next Iteration)

Visual confirmation layer before alert fires:
- **Bookmap** or **Jigsaw** running parallel to algorithm
- Alert triggers only if heatmap shows:
 - Passive absorption at ask (large lot traded without price movement)
 - No iceberg spoofing (sustained bid support, not flash orders)
- Manual "Confirm/Cancel" button for first month of live trading

---

## 9. Operational Schedule (ET)

- **09:25**: Universe refresh (pre-market high-volume scan)
- **09:30–10:30**: Primary window (institutional flow, max 3 trades)
- **10:30–11:30**: Secondary window (max 2 trades)
- **11:30–14:00**: No trading (HFTs retreat, spreads widen, algos dormant)
- **14:00–15:00**: Final window only if morning P&L > $0 (max 2 trades)
- **15:00**: Hard stop (avoid close volatility, T+1 settlement risk)

---

## 10. Risk & Compliance

**PDT Violation**: Maintain $30K+ cushion; never drop below $25K  
**T+1 Free Riding**: Enable IBKR Limited Margin (free) to use unsettled funds  
**Biotech Halts**: Scan exclude: XBI components, stocks with "FDA" in news  
**Commission Drag**: Max 6 trades/day = $21/day fees (budgeted in $1K target)  
**Account Ceiling**: Auto-withdraw profits >$80K account balance to checking

---

## Success Criteria (90-Day Evaluation)

- **Weekly Average**: $700–$1,000 gross. Review if <$500 for 2 consecutive weeks
- **Win Rate**: ≥60%. Below 55% = stop trading, backtest for edge decay
- **Avg Win**: $80–$100 net. Below $60 = slippage problem (reduce size)
- **Max Drawdown**: <$3,000/month (7.5%). >$3K = reduce risk to 0.15%/trade
- **Breakeven Days**: ≤40%. Too many flat days = overtrading; tighten filters

---

## Core Philosophy (Revised)

**This is not a wealth-building system. It is a high-intensity, capacity-constrained job.**

The edge exists only because:
1. You are small enough to fit through the door (400 shares)
2. You are slow enough to avoid HFT detection (human execution)
3. You are fast enough to front-run retail FOMO (order-flow reading)

**Scale kills this edge.** At $100K account, you become the prey. Withdraw profits weekly. Treat the $1,000/week as a salary, not a seed for compounding.

**Trade the inefficiencies institutions ignore. Avoid the arenas where they hunt.**
