What you are building is an **order-flow intelligence platform**. Below is the **complete professional requirements specification** for reaching your stated goal: extracting a small, repeatable daily edge from liquidity dislocations.

---

# SYSTEM OBJECTIVE

Detect **transient order-book imbalances** that statistically precede short-term price continuation, and deliver **actionable, human-executed trade blueprints** with strict quality control.

---

# FUNCTIONAL REQUIREMENTS

## 1. Dynamic Trading Universe

| Feature          | Requirement                                          |
| ---------------- | ---------------------------------------------------- |
| Universe Builder | Auto-build daily symbol universe every 5 minutes     |
| Filters          | Price 10–80, RelVol > 2, Spread < 0.05, Float < 150M |
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

# NON-FUNCTIONAL REQUIREMENTS

| Area          | Requirement               |
| ------------- | ------------------------- |
| Latency       | <500ms ingestion-to-alert |
| Uptime        | 99.9%                     |
| Determinism   | Replayable sessions       |
| Observability | Full event logs           |
| Scalability   | 500 symbols concurrently  |

---

# SUCCESS CRITERIA

| Metric       | Target                |
| ------------ | --------------------- |
| Avg Win      | ≥ 0.45%               |
| Avg Loss     | ≤ −0.30%              |
| Accuracy     | ≥ 62%                 |
| Max Drawdown | ≤ 1.5% daily          |
| Trades/Day   | 3–6 high-quality only |

---

# FINAL TRUTH

This system does not predict price.

It detects **liquidity failure points** — moments where institutions cannot hide their intent.

That is where small, repeatable profits live.
