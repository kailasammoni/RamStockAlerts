# Agentic Workflow

This document defines how Codex should operate in this repo, how instructions are discovered, and how to keep changes verifiable and auditable.

## Instruction discovery
- Codex builds an instruction chain once per run (or session start in the TUI) from global → repo scopes.
- In each directory, `AGENTS.override.md` takes precedence over `AGENTS.md`; optional fallback names can be configured in Team Config.
- Codex skips empty instruction files and enforces a max-bytes cap (configurable via `project_doc_max_bytes`).
- This repo keeps **one** root `AGENTS.md` and no nested overrides.
- If instructions change, restart Codex in the repo root to rebuild the instruction chain.
- Optional audit: run `codex --ask-for-approval never "Summarize the current instructions."` from the repo root to view the active chain.

## Optional team config (not committed)
- Codex can also read team config from `.codex/` folders (e.g., `config.toml`, `rules/`, `skills/`) to standardize defaults.
- Precedence (high → low): `$CWD/.codex/` → parent folders → `$REPO_ROOT/.codex/` → `$CODEX_HOME` → `/etc/codex/`.
- This repo does not check in team config by default; keep it local unless explicitly requested.

## Skill usage
- Repo skills live in `.codex/skills/` and are listed in `SKILLS.md`.
- Use explicit invocation when you want deterministic behavior (e.g., `$ramstockalerts-workflow`).

## Standard workflow
1. **Read instructions**: Start with `AGENTS.md`, then check `SKILLS.md`.
2. **Scope the change**: Keep diffs tight; avoid unrelated refactors.
3. **Implement**: Prefer minimal, reversible changes.
4. **Verify**: Run `powershell -File scripts/verify.ps1` (or `bash scripts/verify.sh`).
5. **Report**: Follow the deliverables format in `AGENTS.md`.

## Safety notes
- No secrets in logs or commits.
- Any signal-firing changes require tests + log evidence.
- Schema/gating changes must update `docs/DataContracts.md` and `docs/DecisionLog.md`.
