# System Charter

## Purpose
RamStockAlerts detects transient liquidity dislocations using Level II market depth and tick-by-tick tape data, producing auditable trade blueprints for human execution.

## Non-goals
- Predicting price direction.
- Technical-indicator driven signals.
- Fully automated execution (unless explicitly requested and safety-reviewed).

## Operating principles
- Scarcity over volume: prefer fewer, higher-quality signals.
- Determinism: recording + replay are first-class for debugging and validation.
- Auditability: append-only JSONL journals for decisions and outcomes.

