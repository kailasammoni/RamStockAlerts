# RamStockAlerts – Small Account Edge System
**Strategic Product Roadmap & Functional Specification**

An order-flow intelligence platform designed to extract a small, repeatable daily edge from liquidity dislocations in sub-institutional environments.

---

## 1. System Objective & Financial Goals
**Primary Goal**: Generate $700–$1,000 gross profit per 5-day trading week (1.4–2% weekly return) on a $40K account.
**Secondary Goal**: Maintain sub-$100K account ceiling to preserve execution edge and avoid HFT detection.

### Core Philosophy
*   **Capacity Constrained**: This is not a wealth-building compounding machine; it is a high-intensity, capacity-constrained tool.
*   **Scale Kills the Edge**: Once the account exceeds $100K, the position sizes required will move the Level 2 ask, alerting competitors and ruining the R/R.
*   **Scarcity over Volume**: Max 6–9 high-quality trades/day. Detect dislocations, not trends.

---

## 2. Core Constraints (Hard Limits)
| Constraint | Limit | Rationale |
| :--- | :--- | :--- |
| **Account Size** | $30K – $80K | PDT protection + liquidity entry/exit speed. |
| **Position Size** | 400 – 600 Shares | Prevents Level 2 exhaustion; avoids moving the ask. |
| **Symbol Universe** | 100 – 150 Max | IBKR pacing limits (100 msg/sec socket constraint). |
| **Max Drawdown** | $600/Day (1.5%) | Psychological circuit breaker + capital preservation. |
| **Risk Per Trade** | 0.25% ($75–$100) | Survives 6 consecutive losses without ruin. |

---

## 3. Trading Universe (The "Liquidity Desert")
**Data Source**: IBKR Socket Client (TWS API) – Native Level I/II.

### Dynamic Filters
*   **Price**: $2.00 – $20.00 (Avoids hard-to-borrow fees and delisting risks).
*   **Float**: 5M – 50M shares (The "sweet spot" for movement vs. fills).
*   **Spread**: $0.02 – $0.05 (Acceptable slippage cost).
*   **Rel Volume**: >2.0 (Ensures tape velocity signals are valid).
*   **Exclusions**: ETFs, ADRs, Warrants, and Biotech (to avoid binary gap-down risks).

---

## 4. Signal Intelligence & Scoring
Signals are calculated on `tickSize()` and `tickPrice()` callbacks.

| Metric | Threshold | Weight |
| :--- | :--- | :--- |
| **Bid-Ask Ratio** | ≥ 2.5:1 (Top 3 levels) | 3 pts |
| **Spread Compression** | Sustained < $0.04 for 2s | 2 pts |
| **Tape Velocity** | ≥ 4 trades/second | 2 pts |
| **Level 2 Depth** | ≥ 1,200 shares at ask | 2 pts |
| **VWAP Reclaim** | Price crossing VWAP upward | 1 pt |
| **TOTAL SCORE** | **Accept if ≥ 8.0/10** | -- |

**Critical Filter**: If Level 2 depth < 1,200 shares at ask, the alert is suppressed to prevent slippage into the next price level.

---

## 5. False Signal Rejection (Anti-Manipulation)
*   **Flash Bid Walls**: >5k shares appearing/canceling within 500ms → **Reject**.
*   **Ask Replenishment**: Ask size refills faster than tape prints (Iceberg selling) → **Reject**.
*   **Spread Blowout**: Spread widens >50% within 2s of trigger → **Cancel**.
*   **Halt Risk**: Price moves >3% in 60 seconds → **Global Halt**.

---

## 6. Trade Blueprint (Execution Model)
Example for a $12 stock with $0.03 spread:
*   **Entry**: Ask + $0.01 (slippage buffer) = $12.04
*   **Stop**: Entry – (Spread × 3) = $11.95 (-$0.09)
*   **Target**: Entry + (Spread × 6) = $12.22 (+$0.18)
*   **Expected Net**: ~$85.00 profit (after $3.50 commission) per 500 shares.

---

## 7. Operational Schedule (ET)
| Time | Phase | Activity |
| :--- | :--- | :--- |
| **09:25** | Universe Build | Pre-market high-volume scan; Redis TTL refresh. |
| **09:30 – 10:30** | Primary Window | Institutional flow focus; Max 3 trades. |
| **10:30 – 11:30** | Secondary Window | Max 2 trades. |
| **11:30 – 14:00** | Market Lull | **No Trading** (HFT dominance, wide spreads). |
| **14:00 – 15:00** | Final Window | Only if Daily P&L > $0; Max 2 trades. |
| **15:00** | Hard Stop | Avoid close volatility and T+1 settlement risks. |

---

## 8. Non-Functional Requirements (Technical SLA)
*   **Latency**: < 500ms ingestion-to-alert delivery.
*   **Uptime**: 99.9% availability during market hours.
*   **Observability**: All signals (accepted/rejected) stored in JSONL for strategy audits.
*   **Determinism**: System must support "Replay Mode" for historical testing.

---

## 9. Validation Protocol (Go-to-Market)
1.  **Phase 1 (Shadow)**: 100-share lots. Target 60% win rate over 50 signals.
2.  **Phase 2 (Micro)**: 200–300 shares. Prove $200/week consistency.
3.  **Phase 3 (Full)**: 400–500 shares. Target $700–$1,000/week.
*   **Abort Condition**: If Phase 1 shows <55% win rate after 50 signals, engine is fundamentally flawed.

---

### Success Criteria (90-Day Evaluation)
*   **Profit Factor**: ≥ 2.0 (minimum survival), Target 2.5 (edge validated)
*   **Win Rate**: ≥ 60%
*   **Avg Net Win**: $80 – $100
*   **Max Monthly Drawdown**: < $3,000 (7.5% of account).

Abort Condition: Profit Factor < 1.5 after 100 trades (edge non-existent, stop trading)

