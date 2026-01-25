# AGENTS.md — RamStockAlerts agent contract

## Mission (do not drift)
Build an order-flow intelligence platform that detects transient liquidity dislocations using IBKR Level II depth + tape, producing high-quality human-executed trade blueprints.

Scarcity > frequency: target 3–6 signals/day (hard cap 36/day).

No prediction. No indicators. No momentum chasing.

## Guardrails (money-touching rules)
- Never log or commit secrets (IBKR creds, tokens, Discord webhooks).
- Also update telemetry/diagnostics when adding features where ever it touches signals and orders.
- Any change that affects signal firing must add/adjust tests and include log evidence.
- Keep diffs small; do not reformat unrelated files.

## How to run / verify (always do before final answer)
- Setup (Ubuntu runner): `./setup-jules.sh`
- Verify (single command): `powershell -File scripts/verify.ps1` (Windows) or `bash scripts/verify.sh` (Linux)
- Optional replay smoke test: `powershell -File scripts/replay.ps1 -Symbol AAPL`

## Repo conventions
- When changing schemas or meaningfully changing gating/scoring, update:
  - `docs/DataContracts.md`
  - `docs/DecisionLog.md` (if present)

## Deliverables format (in your response)
1) What changed (files + intent)
2) Why it changed (product/system reason)
3) How verified (commands + key log lines)
4) Risk notes (what could break)
