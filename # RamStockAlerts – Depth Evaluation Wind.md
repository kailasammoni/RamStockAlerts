# RamStockAlerts – Depth Evaluation Window Policy

Status: Authoritative  
Applies to: MarketDataSubscriptionManager, UniverseService, IBkrMarketDataClient

---

## 1. Definitions (Concrete)

- **Candidate**  
  Symbol returned by scanner and passing classification filters.

- **Probe Symbol**  
  Candidate subscribed to Level I (and optional tape).

- **Evaluation Symbol**  
  Probe symbol temporarily upgraded to:
  - Market Depth
  - Tick-by-Tick (if available)

Only Evaluation Symbols may drive strategy decisions.

---

## 2. Hard System Constraints

- Max concurrent Evaluation Symbols = `DepthSlots` (currently 3)
- Depth is **never permanent**
- Tape-only symbols do **not** participate in strategy

---

## 3. Evaluation Window Lifecycle

### 3.1 Entry (Upgrade)
A symbol MAY be upgraded to Evaluation if:

- It is already a Probe Symbol
- DepthSlots available
- Classification + contract resolution is complete
- Not currently in cooldown

**Action**
- Upgrade existing subscription (do NOT resubscribe L1)
- Start evaluation timer
- Mark `evaluationStartMs`

---

### 3.2 During Evaluation
While evaluating:

- Depth + tape data are ingested
- Strategy + gating logic may run
- Signals may be emitted (shadow mode)

**Constraints**
- Evaluation duration is bounded (e.g. 60–180s)
- Data freshness gates must pass
- No extension of window based on “almost good” signals

---

### 3.3 Exit (Mandatory)
Evaluation ends when **any** occurs:

- Signal emitted (accepted or rejected)
- Evaluation timeout reached
- Data invalid (stale tape, broken book)
- Manual or system abort

**Action**
- Cancel depth subscription
- Cancel tick-by-tick
- Record evaluation outcome
- Start cooldown timer

---

## 4. Cooldown Rule

- Symbols exiting evaluation enter cooldown
- Cooldown prevents immediate re-upgrade
- Cooldown duration is fixed and logged

Cooldown does NOT prevent probe-level subscriptions.

---

## 5. Forbidden States (System Errors)

The following indicate bugs:

- Depth active without evaluation timer
- Evaluation exceeding max duration
- More than `DepthSlots` active evaluations
- Strategy firing on non-evaluation symbols

These MUST be logged as errors.

---

## 6. Observability Requirements

Every evaluation window MUST log:

- symbol
- evaluationStartMs
- evaluationEndMs
- durationMs
- exitReason (Signal, Timeout, DataInvalid)
- depthMinutesConsumed

If this data does not exist, the evaluation is considered invalid.
