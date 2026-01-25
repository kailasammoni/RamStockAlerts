<!--
Sync Impact Report:
- Version change: template -> 0.1.0
- Modified principles: N/A (initial population)
- Added sections: Core Principles (filled), Additional Constraints, Development Workflow, Governance (filled)
- Removed sections: None
- Templates requiring updates:
  - .specify/templates/plan-template.md (ok, no changes required)
  - .specify/templates/spec-template.md (ok, no changes required)
  - .specify/templates/tasks-template.md (ok, no changes required)
  - .specify/templates/commands/*.md (pending: directory not present)
- Deferred placeholders: TODO(RATIFICATION_DATE)
-->
# RamStockAlerts Constitution

## Core Principles

### Order-Flow Blueprints Only
Detect transient liquidity dislocations using IBKR Level II depth plus tape data,
and output auditable trade blueprints for human execution. No price prediction,
no indicator-driven signals, no momentum chasing, and no auto-execution unless
explicitly requested and safety-reviewed.

### Scarcity Over Frequency
Target 3-6 signals per day with a hard cap of 36. Prefer fewer, higher-confidence
signals over volume.

### Deterministic, Auditable Evidence
Recording and replay are first-class. Signal decisions must be reproducible and
traceable via append-only journals/logs so that every alert has explainable
evidence.

### Money-Touching Safety
Never log or commit secrets (IBKR credentials, tokens, webhooks). Any change that
affects signal firing must add/adjust tests and include log evidence. When
features touch signals or orders, also update telemetry and diagnostics.

### Small Diffs, Documented Contracts
Keep diffs small and avoid unrelated refactors. When schemas or gating/scoring
changes, update docs/DataContracts.md and docs/DecisionLog.md to keep contracts
and rationale aligned.

## Additional Constraints

- No automated execution without explicit request and safety review.
- Preserve append-only decision journals and JSONL evidence trails.
- Keep local-only overrides out of git (see .gitignore and AGENTS.override.md).

## Development Workflow

- Source layout is a single project with src/ and tests/ at repo root.
- Follow docs/AgenticWorkflow.md for planning and execution discipline.
- Verification is required before reporting completion:
  - Windows: powershell -File scripts/verify.ps1
  - Linux: bash scripts/verify.sh
- Optional replay smoke test: powershell -File scripts/replay.ps1 -Symbol AAPL

## Governance

- This constitution supersedes other guidance. Conflicts must be resolved by
  updating this document and re-syncing dependent templates.
- Amendments require a version bump (semver), updated Sync Impact Report, and
  review for compliance with money-touching guardrails.
- Compliance review is mandatory for changes affecting signals, orders, schemas,
  or telemetry.

**Version**: 0.1.0 | **Ratified**: TODO(RATIFICATION_DATE): original adoption date unknown | **Last Amended**: 2026-01-25