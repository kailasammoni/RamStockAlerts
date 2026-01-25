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
