# RamStockAlerts — Authoritative Market Data & Evaluation Policy
Status: Authoritative (production intent)
Applies to: UniverseService, MarketDataSubscriptionManager, IBkrMarketDataClient, ShadowTradingCoordinator

## 0) Goal
Detect transient liquidity dislocations using IBKR tape + Level II depth.
Scarcity > frequency. Target: 3–6 high-quality signals/day.

## 1) System Sets (must be explicit in logs)
The system maintains four sets of symbols:

1. CandidateSet
   - Raw symbols from scanner output.

2. EligibleSet
   - CandidateSet filtered to tradable common stocks (exclude ETFs/ETNs/other).

3. ProbeSet
   - Symbols subscribed to Level 1 / base market data (tape via LAST/LAST_SIZE fallback).
   - Size is limited by IBKR market data lines (e.g., <= 80).

4. EvalSet
   - Symbols upgraded to Depth + Tick-by-Tick.
   - Hard limit: EvalSlots = 3.
   - Only EvalSet symbols may drive strategy decisions.

ActiveUniverse = EvalSet ∩ ReadyByGates

## 2) Hard Constraints (must be enforced)
- EvalSlots is fixed (default 3).
- Depth subscriptions are temporary evaluation windows, never permanent.
- Strategy MUST NOT execute on non-evaluation symbols.
- Tape freshness gating MUST be based on local receipt time.

## 3) Time Sources (authoritative)
For every tape tick:
- lastTapeRecvMs = local receipt timestamp (authoritative for gating)
- lastTapeEventMs = exchange/event timestamp (analytics only)
- skewMs = lastTapeRecvMs - lastTapeEventMs (observability)

Freshness gates use:
- nowMs - lastTapeRecvMs <= staleWindowMs

## 4) Evaluation Window Policy
Each EvalSet symbol is evaluated for a bounded window (EvalWindowMs).

Entry:
- Symbol must already be in ProbeSet.
- Upgrade must preserve existing L1 subscription requestId.

Exit (any):
- Signal emitted (accepted or rejected)
- Evaluation timeout reached
- Data invalid beyond threshold (stale tape, invalid book)

On exit:
- Cancel depth + tick-by-tick
- Keep probe subscription unless symbol dropped from EligibleSet
- Apply depth cooldown (DepthCooldownMs) before it can re-enter EvalSet

## 5) Signal Emission Rules (anti-spam)
Signals are emitted only on state transitions, not continuously.
Acceptable triggers:
- Score crosses threshold from below to above
- A key condition flips (e.g., new absorption event detected)

Cooldown is secondary protection, not the primary spam limiter.

## 6) Universe Refresh Semantics
UniverseService produces snapshot refreshes.
MarketDataSubscriptionManager diffs snapshot-to-snapshot:
- Add new eligible symbols to ProbeSet until probe capacity reached
- Remove symbols that fell out of EligibleSet unless currently in EvalSet
- EvalSet symbols are not evicted mid-window except for hard invalidation

## 7) Required Logs / Metrics
Every refresh cycle MUST log:
- CandidateSet size
- EligibleSet size
- ProbeSet size (active market data lines)
- EvalSet symbols
- ActiveUniverse symbols
- evaluation outcomes (durationMs, exitReason)

Every evaluation MUST log:
- symbol, startMs, endMs, durationMs
- exitReason (Signal, Timeout, InvalidData)
- lastTapeRecvMs and staleness at exit
