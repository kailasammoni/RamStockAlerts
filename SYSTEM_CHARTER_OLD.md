# SYSTEM_CHARTER.md

## 🧭 System Goal (Locked)
Build an order-flow intelligence platform that detects transient liquidity dislocations using IBKR Level II depth + tape, and produces high-quality, human-executed trade signals.

- **Strategy class**: Order-flow / market microstructure
- **Edge source**: Order book imbalance + absorption + tape acceleration
- **No** prediction, indicators, or momentum chasing
- **Scarcity > frequency** → target 3–6 trades/day max

---

## 🚀 MVP Status

**✅ MVP is live and running in shadow mode**

- Order book and tape feeds from IBKR are connected
- Signal engine is processing real-time data continuously
- Trade blueprints (entry/stop/target) are generated
- Alerts are evaluated and scored with post-signal filters
- All accepted/rejected signals logged to structured shadow journal
- Tape and depth subscriptions are now paired by the market data manager, 10092 depth ineligibility is surfaced via `HandleIbkrError`, and `DepthEligibilityCache` prevents repeated depth retries.
- SchemaVersion=2 shadow journal entries with heartbeat markers keep replay formatting stable, and the `MODE=record` recorder is available to capture raw IBKR depth/tape for diagnostics.

---

## ⚙️ Signal Engine

- **Live scoring system** (QI, absorption, tape accel, spread, walls, VWAP bonus)
- **Post-signal rejection filters**:
  - Spoofing: large cancels at ask
  - Replenishment: ask refill after fills
  - Tape stall: lack of recent trades
- **Not yet implemented**: spread blowout post-trigger, cancel-on-tape-freeze

---

## 📉 Risk Management

- Stop = Entry − 4×Spread
- Target = Entry + 8×Spread
- No dynamic sizing yet (risk % per trade TBD)

---

## 📊 Scarcity Controls

- Max 6 trades/day
- Max 1 per ticker/day
- Lower-ranked signals automatically dropped

---

## 🔔 Execution

- Currently running in shadow (simulation) mode
- No real trades or Discord alerts yet
- All signals journaled to disk
- `MODE=record` can launch the recorder-only service for depth/tape capture instead of the full trading stack.

---

## 🐞 Known Issues

- 10092 depth ineligibility still occurs for symbols without complete contract details or when IB enforces its depth limits; we disable depth for the symbol until `DepthEligibilityCache` revalidates it, but coverage gaps remain.
- Tick-by-tick subscriptions are capped by `MarketData:TickByTickMaxSymbols` (default 10) because of IB limits; new tick-by-tick requests skip until an active symbol is dropped.
- Book validity edge cases (crossed book, stale quotes) and scanner relaunch glitches continue to require manual attention.
- Some journal fields are still defaulting while the schema is being tuned, so rejects and outcomes sometimes need extra cleanup when replaying.


---
