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
- Build: `dotnet build RamStockAlerts.sln`
- All tests: `dotnet test RamStockAlerts.sln`
- Single test: `dotnet test --filter "FullyQualifiedName~TestMethodName"`
- Verify (single command): `powershell -File scripts/verify.ps1` (Windows) or `bash scripts/verify.sh` (Linux)
- Optional replay smoke test: `powershell -File scripts/replay.ps1 -Symbol AAPL`

## Architecture
- .NET 8/10 solution with SQLite (`ramstockalerts.db`) + JSONL journaling
- `src/RamStockAlerts/` — API host, signal engine, telemetry
- `src/RamStockAlerts.Execution/` — Risk validation, order placement
- `lib/ibkr/CSharpClient/` — Vendored IBKR API
- See `docs/Architecture.md` and `CODEBASE_FLOW.md` for details

## Code style
- 4-space indentation, UTF-8, LF line endings (see `.editorconfig`)
- C# naming: PascalCase types/methods, camelCase locals, _camelCase private fields
- No secrets in logs; keep diffs minimal; add tests for signal-affecting changes

## Repo conventions
- When changing schemas or meaningfully changing gating/scoring, update:
  - `docs/DataContracts.md`
  - `docs/DecisionLog.md` (if present)

## Agentic workflow
- Architecture overview: `docs/Architecture.md`
- Skills index: `SKILLS.md`
- Repo has a single root `AGENTS.md` (no nested overrides).

## Deliverables format (in your response)
1) What changed (files + intent)
2) Why it changed (product/system reason)
3) How verified (commands + key log lines)
4) Risk notes (what could break)
