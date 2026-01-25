# Data Contracts (schemas are law)

This system uses append-only JSONL files as an audit trail. Changes here must be intentional and versioned.

## `logs/shadow-trade-journal.jsonl` (ShadowTradeJournalEntry)
- Serialization: System.Text.Json default (PascalCase property names).
- Top-level fields (selected):
  - `SchemaVersion` (int)
  - `DecisionId` (guid), `SessionId` (guid)
  - `EntryType` (string), `Source` (string)
  - `MarketTimestampUtc` / `DecisionTimestampUtc` / `JournalWriteTimestampUtc` (ISO timestamps)
  - `TradingMode` (string; default `Signals`), `Symbol` (string), `Direction` (string)
  - `DecisionOutcome` (string), `RejectionReason` (string)
  - `DataQualityFlags` (string[])
  - `ObservedMetrics`, `DecisionInputs`, `Blueprint`, `SystemMetrics`, `GateTrace`, `UniverseUpdate` (objects; see `Models/ShadowTradeJournalEntry.cs`)

## `logs/trade-outcomes.jsonl` (TradeOutcome)
- Serialization: System.Text.Json default (PascalCase property names).
- Fields:
  - `DecisionId` (guid), `Symbol` (string), `Direction` (string)
  - `EntryPrice` / `StopPrice` / `TargetPrice` / `ExitPrice` (decimal?)
  - `OutcomeLabeledUtc` (ISO timestamp)
  - `OutcomeType` (string: `HitTarget`/`HitStop`/`NoHit`/`NoExit`)
  - `DurationSeconds` (long?)
  - `PnlUsd` (decimal?), `RiskMultiple` (decimal?), `IsWin` (bool?)
  - `QualityFlags` (string[]), `SchemaVersion` (int)

## `logs/execution-ledger.jsonl` (JsonlExecutionLedger)
- Serialization: System.Text.Json Web defaults (camelCase property names).
- Envelope:
  - `type` (string: `intent`/`bracket`/`result`)
  - `timestampUtc` (ISO timestamp)
  - `payload` (object; contract depends on `type`)

## `logs/ibkr-depth-<SYMBOL>-YYYYMMDD.jsonl` / `logs/ibkr-tape-<SYMBOL>-YYYYMMDD.jsonl`
- Serialization: System.Text.Json Web defaults (camelCase property names).
- Each line is one recorded event with `eventType` plus event-specific fields.

