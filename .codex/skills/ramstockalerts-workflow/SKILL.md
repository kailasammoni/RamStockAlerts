---
name: ramstockalerts-workflow
description: Use for repo changes that require verification, documentation updates, or path/layout awareness.
metadata:
  short-description: RamStockAlerts workflow
---

# RamStockAlerts Workflow

## Goal
Apply changes safely and keep documentation + verification aligned with the repo’s money‑touching constraints.

## Checklist
1. Read `AGENTS.md` and confirm no nested overrides apply.
2. Confirm the `src/` + `tests/` layout for any path references.
3. Keep diffs small; avoid unrelated refactors.
4. Update docs when behavior or schemas change (`docs/DataContracts.md`, `docs/DecisionLog.md`).
<!-- 5. Run `powershell -File scripts/verify.ps1` (or `bash scripts/verify.sh`). -->
6. Report using the deliverables format in `AGENTS.md`.
