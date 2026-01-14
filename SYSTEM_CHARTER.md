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

---

## 🐞 Known Issues

- Book validity edge cases (crossed book, stale quotes)
- Rejection filters still being hardened
- Scanner relaunch on IBKR startup has minor glitches
- Journal includes some default/missing values (under review)

---
