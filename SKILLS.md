# RamStockAlerts — Skills Index

This repo ships a small set of **repo-scoped** Codex skills under `.codex/skills/`. Codex loads skills by scope and precedence, overriding lower-precedence skills with the same name. It only loads each skill’s `name` + `description` until the skill is explicitly invoked.

## Repo skills
- **`ramstockalerts-workflow`** — Use for repo changes that require verification, documentation updates, or path/layout awareness (src/tests).

## Skill locations (high → low)
- **Repo**: `$CWD/.codex/skills`
- **Repo**: `$CWD/../.codex/skills` (parent folders above CWD in a repo)
- **Repo**: `$REPO_ROOT/.codex/skills`
- **User**: `$CODEX_HOME/skills` (defaults to `~/.codex/skills`)
- **System**: `/etc/codex/skills`

## Add or update a skill
1. Create a folder under `.codex/skills/<skill-name>/`.
2. Add a `SKILL.md` with `name` and `description` in YAML front matter.
3. Restart Codex so it reloads skills.

## Tips
- Invoke explicitly with `$skill-name` (or `/skills` to list), or rely on implicit selection for matching tasks.
- Keep skills small and focused; prefer instructions over scripts.
- Use explicit invocation for deterministic workflows: `$ramstockalerts-workflow`.
