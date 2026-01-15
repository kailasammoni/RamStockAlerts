# UniverseUpdate Journal Entry

## Purpose

The `UniverseUpdate` journal entry type provides a scientific audit trail of universe refresh cycles. It is emitted once per `ApplyUniverseAsync` call in the `MarketDataSubscriptionManager` and captures:

1. **What candidates were considered** (top 20 to prevent spam)
2. **What symbols made it to ActiveUniverse** (≤3 symbols with tape + depth + tick-by-tick)
3. **What symbols were excluded and why** (NoDepth, NoTickByTick, NoTape, Unknown)
4. **Verification counts** to ensure invariants hold

## Schema

The entry uses `SchemaVersion=1` for the `UniverseUpdateSnapshot` and follows the journal's schema v2.

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `EntryType` | string | Always `"UniverseUpdate"` |
| `Source` | string | Always `"MarketDataSubscriptionManager"` |
| `SessionId` | Guid | Journal session identifier |
| `MarketTimestampUtc` | DateTimeOffset | Timestamp when universe refresh occurred |
| `SchemaVersion` | int | Journal schema version (currently 2) |
| `UniverseUpdate` | object | Nested snapshot with schema version 1 |

### UniverseUpdate Snapshot Fields

| Field | Type | Description |
|-------|------|-------------|
| `SchemaVersion` | int | Always `1` for this snapshot |
| `NowMs` | long | Unix timestamp in milliseconds |
| `NowUtc` | DateTimeOffset | UTC timestamp |
| `Candidates` | List&lt;string&gt; | Top 20 candidate symbols (limited to prevent spam) |
| `ActiveUniverse` | List&lt;string&gt; | Symbols that meet ActiveUniverse contract (≤3) |
| `Exclusions` | List&lt;UniverseExclusion&gt; | Symbols excluded from ActiveUniverse with reasons |
| `Counts` | UniverseCounts | Verification counts for invariant checking |

### UniverseExclusion Fields

| Field | Type | Description |
|-------|------|-------------|
| `Symbol` | string | Symbol that was excluded |
| `Reason` | string | Exclusion reason: `NoDepth`, `NoTickByTick`, `NoTape`, `Unknown` |

### UniverseCounts Fields

| Field | Type | Description |
|-------|------|-------------|
| `CandidatesCount` | int | Total number of candidates considered (can exceed 20 if limited) |
| `ActiveCount` | int | Number of symbols in ActiveUniverse (≤3 by default) |
| `DepthCount` | int | Number of symbols with depth subscriptions |
| `TickByTickCount` | int | Number of symbols with tick-by-tick subscriptions |
| `TapeCount` | int | Number of symbols with tape (market data) subscriptions |

## Example JSON Output

```json
{
  "EntryType": "UniverseUpdate",
  "Source": "MarketDataSubscriptionManager",
  "SessionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "MarketTimestampUtc": "2026-01-15T14:30:00.123456Z",
  "SchemaVersion": 2,
  "JournalWriteTimestampUtc": "2026-01-15T14:30:00.234567Z",
  "UniverseUpdate": {
    "SchemaVersion": 1,
    "NowMs": 1736952600123,
    "NowUtc": "2026-01-15T14:30:00.123456Z",
    "Candidates": [
      "AAPL", "MSFT", "GOOGL", "AMZN", "NVDA",
      "TSLA", "META", "BRK.B", "TSM", "UNH",
      "LLY", "V", "JPM", "WMT", "XOM",
      "AVGO", "MA", "PG", "JNJ", "COST"
    ],
    "ActiveUniverse": [
      "AAPL", "MSFT", "GOOGL"
    ],
    "Exclusions": [
      {
        "Symbol": "AMZN",
        "Reason": "NoDepth"
      },
      {
        "Symbol": "NVDA",
        "Reason": "NoDepth"
      }
    ],
    "Counts": {
      "CandidatesCount": 25,
      "ActiveCount": 3,
      "DepthCount": 3,
      "TickByTickCount": 3,
      "TapeCount": 5
    }
  }
}
```

## Invariant Checks

The `Counts` object enables verification of expected invariants:

1. **ActiveCount ≤ DepthCount**: Active symbols must have depth
2. **ActiveCount ≤ TickByTickCount**: Active symbols must have tick-by-tick
3. **ActiveCount ≤ TapeCount**: Active symbols must have tape
4. **ActiveCount ≤ MaxDepthSymbols**: Typically ≤ 3 by default configuration
5. **Candidates.Count ≤ 20**: List is truncated to prevent spam (but CandidatesCount shows actual total)
6. **ActiveCount + Exclusions.Count ≤ TapeCount**: Accounts for all tape subscriptions

## Exclusion Reasons

| Reason | Description |
|--------|-------------|
| `NoDepth` | Symbol has tape but no depth subscription (didn't make top N candidates) |
| `NoTickByTick` | Symbol has depth but tick-by-tick is unavailable (e.g., error 10190) |
| `NoTape` | Symbol has no tape subscription (shouldn't normally occur in exclusions) |
| `Unknown` | Symbol is excluded for an unexpected reason (investigative flag) |

## Usage

The journal entry is automatically emitted when `TradingMode=Shadow` and `IShadowTradeJournal` is configured. No manual intervention is required.

### Log Output

When a `UniverseUpdate` entry is written to the journal file, a structured log message is also emitted:

```
JournalWrite: type=UniverseUpdate schema=2 candidates=25 active=3 exclusions=2
```

This provides a quick summary without requiring JSONL file inspection.

## File Location

Journal entries are appended to the file specified by `ShadowTradeJournal:FilePath` configuration (default: `logs/shadow-trade-journal.jsonl`).

Each line in the file is a complete JSON object representing one journal entry.
