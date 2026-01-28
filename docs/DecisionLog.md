# Decision Log

Record meaningful strategy/logic changes here (date, what changed, why, risks, and how verified).

## Template
- Date (UTC):
- Area:
- Change:
- Why:
- Verification:
- Risk notes:

## 2026-01-25
- Area: Execution tracking, risk gates, signal validation
- Change: Added IBKR order status/fill tracking via OrderStateTracker, enforced hard-gate signal thresholds, post-signal monitoring cancellations, corrected P&L and risk multiple calculations, and tightened ledger/risk daily limits and bracket state tracking.
- Why: Ensure order lifecycle visibility, enforce stated signal quality gates, and protect daily risk budgets with accurate P&L.
- Verification: Planned unit tests for tracker/ledger, hard gates, and post-signal monitoring.
- Risk notes: Order status mapping and post-signal cancellation rely on IBKR callbacks; if callbacks lag, bracket state updates and cancellation timing may be delayed.

## 2026-01-25
- Area: Monitoring enhancements and execution controls
- Change: Added pre-fill monitoring, post-signal throttling, stale-order alerts, monitor-only execution mode, and quality band tagging.
- Why: Improve safety during volatile sessions, reduce monitoring load, and enable cohort analysis without shutting down signal detection.
- Verification: Added/updated unit tests for pre-fill cancellations and monitor-only behavior.
- Risk notes: Monitor-only mode relies on shared options state; operators should ensure toggles are coordinated across instances.

## 2026-01-28
- Area: Signal gating
- Change: Allow per-symbol cooldown bypass when confidence meets `Signals:CooldownBypassScore` (default 90), and log when a bypass occurs.
- Why: Ensure top-scoring order-flow signals are not suppressed by cooldown when conviction is high.
- Verification: Added unit tests covering cooldown bypass acceptance and cooldown rejection for lower confidence.
- Risk notes: Overly aggressive bypass thresholds could increase signal frequency during clustered events; monitor daily caps.
