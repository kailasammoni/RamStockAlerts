# Architecture

## Overview
RamStockAlerts is an order‑flow intelligence platform built on .NET 8/10 that ingests IBKR Level II depth and tick‑by‑tick tape data to produce human‑executed trade blueprints. It prioritizes scarcity over frequency and emphasizes deterministic replay and auditability.

## Repo layout (current)
```
src/
  RamStockAlerts/                 # API host + signal engine
  RamStockAlerts.Execution/       # Execution module
tests/
  RamStockAlerts.Tests/
  RamStockAlerts.Execution.Tests/
lib/
  ibkr/CSharpClient/              # Vendored IBKR API
docs/                             # System + ops documentation
```

## Core components
- **API Host** (`src/RamStockAlerts`): Controllers, signal loop, journaling, telemetry.
- **Execution Module** (`src/RamStockAlerts.Execution`): Risk validation and (optional) order placement.
- **Journaling**: JSONL append‑only logs for decisions and outcomes.
- **Replay/Record**: Deterministic replay for validation and debugging.

## System flows
See `CODEBASE_FLOW.md` for the detailed sequence diagrams and `CODEBASE_DOCUMENTATION.md` for exhaustive component references.
