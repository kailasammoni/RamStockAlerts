# Runbook

## Modes
- **API Host (default)**: ASP.NET Core API (shadow trading and admin endpoints).
- **Shadow mode**: generates trade blueprints without execution (default behavior).
- **Record mode**: records IBKR depth + tape to JSONL files in `logs/`.
- **Replay mode**: replays recorded JSONL deterministically for validation.
- **Diagnostics mode**: subscription health checks.

## Startup checklist (live data)
1. IB Gateway/TWS running and API enabled (read-only recommended).
2. Market data subscriptions active (depth + tick-by-tick for target venues).
3. Verify tape is flowing (recent prints).
4. Verify depth is flowing (depth updates arriving; book not stale/crossed).

## Common failures
- No depth updates: check entitlements, exchange routing, and `reqMktDepth` params.
- Tape missing: ensure tick-by-tick permissions; confirm fallback path if configured.
- Symbols outside scope: tighten universe filters (exclude ETFs/ADRs/halts).

## Run from a stable folder (Windows)
Core principle: do not run the app from the working git directory; publish and run from an output folder to avoid file locks and branch-switch surprises.

Example layout:
- `C:\\workspace\\RamStockAlerts\\` (git working directory)
- `C:\\run\\RamStockAlerts\\shadow\\` (stable running instance)

