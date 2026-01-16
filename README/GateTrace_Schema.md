# GateTrace Schema Documentation

## Overview

`GateTrace` is a deterministic snapshot emitted alongside rejection entries in the Shadow Trade Journal v2. It captures numeric context about tape, depth, and configuration state when a symbol is rejected at the gate level (before reaching scoring/ranking).

## Purpose

When we see rejections like `NotReady_TapeNotWarmedUp` or `NotReady_NoDepth`, we need concrete numeric evidence to diagnose:
- Why was the gate triggered?
- What were the exact values at the time?
- How far off was the symbol from passing the gate?

## Schema Version

**Current Version:** 1

Schema versioning allows evolution of the GateTrace structure without breaking backward compatibility.

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `SchemaVersion` | `int` | Yes | Schema version (currently 1) |
| `NowMs` | `long` | Yes | Current timestamp (Unix epoch milliseconds) when gate check occurred |
| **Tape Context** | | | |
| `LastTradeMs` | `long?` | No | Timestamp of last trade received (null if no trades) |
| `TradesInWarmupWindow` | `int` | Yes | Number of trades within warmup window |
| `WarmedUp` | `bool` | Yes | Whether tape warmup criteria met |
| `StaleAgeMs` | `long?` | No | Age of last trade in milliseconds (null if no trades) |
| **Depth Context** | | | |
| `LastDepthMs` | `long?` | No | Timestamp of last depth update (null if no depth) |
| `DepthAgeMs` | `long?` | No | Age of last depth update in milliseconds |
| `DepthRowsKnown` | `int?` | No | Number of depth levels known (max of bids/asks) |
| `DepthSupported` | `bool` | Yes | Whether depth subscription is enabled for this symbol |
| **Config Snapshot** | | | |
| `WarmupMinTrades` | `int` | Yes | Configured minimum trades for warmup |
| `WarmupWindowMs` | `int` | Yes | Configured warmup window duration (ms) |
| `StaleWindowMs` | `int` | Yes | Configured tape staleness threshold (ms) |
| `DepthStaleWindowMs` | `int` | Yes | Configured depth staleness threshold (ms) |

## Sample JSON

### Example 1: Tape Not Warmed Up

```json
{
  "SchemaVersion": 2,
  "DecisionId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "EntryType": "Rejection",
  "Symbol": "AAPL",
  "RejectionReason": "NotReady_TapeNotWarmedUp",
  "MarketTimestampUtc": "2026-01-15T07:31:50Z",
  "GateTrace": {
    "SchemaVersion": 1,
    "NowMs": 1768480310000,
    "LastTradeMs": 1768480308000,
    "TradesInWarmupWindow": 3,
    "WarmedUp": false,
    "StaleAgeMs": 2000,
    "LastDepthMs": 1768480309000,
    "DepthAgeMs": 1000,
    "DepthRowsKnown": 5,
    "DepthSupported": true,
    "WarmupMinTrades": 5,
    "WarmupWindowMs": 10000,
    "StaleWindowMs": 5000,
    "DepthStaleWindowMs": 2000
  }
}
```

**Diagnosis**: Symbol rejected because only 3 trades in warmup window, but config requires 5. Last trade was 2 seconds ago (fresh). Depth is healthy (1 second old).

---

### Example 2: Tape Stale

```json
{
  "SchemaVersion": 2,
  "DecisionId": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
  "EntryType": "Rejection",
  "Symbol": "MSFT",
  "RejectionReason": "NotReady_TapeStale",
  "MarketTimestampUtc": "2026-01-15T07:32:15Z",
  "GateTrace": {
    "SchemaVersion": 1,
    "NowMs": 1768480335000,
    "LastTradeMs": 1768480320000,
    "TradesInWarmupWindow": 8,
    "WarmedUp": false,
    "StaleAgeMs": 15000,
    "LastDepthMs": 1768480334000,
    "DepthAgeMs": 1000,
    "DepthRowsKnown": 5,
    "DepthSupported": true,
    "WarmupMinTrades": 5,
    "WarmupWindowMs": 10000,
    "StaleWindowMs": 5000,
    "DepthStaleWindowMs": 2000
  }
}
```

**Diagnosis**: Rejected because last trade is 15 seconds old, exceeding the 5-second staleness threshold. Depth is fresh (1 second old).

---

### Example 3: No Trades Yet

```json
{
  "SchemaVersion": 2,
  "DecisionId": "c3d4e5f6-a7b8-9012-cdef-123456789012",
  "EntryType": "Rejection",
  "Symbol": "GOOGL",
  "RejectionReason": "NotReady_TapeNotWarmedUp",
  "MarketTimestampUtc": "2026-01-15T07:30:00Z",
  "GateTrace": {
    "SchemaVersion": 1,
    "NowMs": 1768480200000,
    "LastTradeMs": null,
    "TradesInWarmupWindow": 0,
    "WarmedUp": false,
    "StaleAgeMs": null,
    "LastDepthMs": 1768480199000,
    "DepthAgeMs": 1000,
    "DepthRowsKnown": 5,
    "DepthSupported": true,
    "WarmupMinTrades": 5,
    "WarmupWindowMs": 10000,
    "StaleWindowMs": 5000,
    "DepthStaleWindowMs": 2000
  }
}
```

**Diagnosis**: No trades received yet (`LastTradeMs` is null). Depth is available and fresh.

---

### Example 4: Depth Not Supported

```json
{
  "SchemaVersion": 2,
  "DecisionId": "d4e5f6a7-b8c9-0123-def1-234567890123",
  "EntryType": "Rejection",
  "Symbol": "AMZN",
  "RejectionReason": "NotReady_NoDepth",
  "MarketTimestampUtc": "2026-01-15T07:33:00Z",
  "GateTrace": {
    "SchemaVersion": 1,
    "NowMs": 1768480380000,
    "LastTradeMs": 1768480378000,
    "TradesInWarmupWindow": 12,
    "WarmedUp": true,
    "StaleAgeMs": 2000,
    "LastDepthMs": null,
    "DepthAgeMs": null,
    "DepthRowsKnown": null,
    "DepthSupported": false,
    "WarmupMinTrades": 5,
    "WarmupWindowMs": 10000,
    "StaleWindowMs": 5000,
    "DepthStaleWindowMs": 2000
  }
}
```

**Diagnosis**: Tape is healthy (warmed up, fresh), but depth is not enabled for this symbol.

---

## Feature Toggle

GateTrace emission is controlled via configuration:

```json
{
  "ShadowTradeJournal": {
    "FilePath": "logs/shadow-trade-journal.jsonl",
    "EmitGateTrace": true
  }
}
```

- **Default**: `true` (enabled)
- **To disable**: Set `EmitGateTrace: false` to reduce log noise

When disabled, the `GateTrace` field will be `null` in rejection entries.

---

## Usage Examples

### Querying Journal for Gate Failures

**Find all tape warmup failures:**
```bash
jq 'select(.RejectionReason == "NotReady_TapeNotWarmedUp") | {Symbol, TradesInWindow: .GateTrace.TradesInWarmupWindow, Required: .GateTrace.WarmupMinTrades}' shadow-trade-journal.jsonl
```

**Find symbols repeatedly failing depth staleness:**
```bash
jq 'select(.GateTrace.DepthAgeMs > .GateTrace.DepthStaleWindowMs) | {Symbol, DepthAge: .GateTrace.DepthAgeMs}' shadow-trade-journal.jsonl
```

**Calculate average warmup window trade count across rejections:**
```bash
jq -s '[.[] | select(.GateTrace != null) | .GateTrace.TradesInWarmupWindow] | add / length' shadow-trade-journal.jsonl
```

---

## Deterministic Output Guarantees

1. **No Implicit Defaults**: All fields are explicitly set. Nullable fields are set to `null` when data unavailable.
2. **Schema Versioning**: `SchemaVersion` field allows future evolution without breaking parsers.
3. **Timestamp Consistency**: All timestamps use Unix epoch milliseconds (same timebase).
4. **Config Snapshot**: Configuration values are captured at rejection time, enabling post-hoc analysis of config changes.

---

## Implementation Notes

- **Emitted alongside rejections**: When `TryLogGatingRejection` creates a rejection entry, it also builds and attaches a `GateTrace` if `EmitGateTrace` is enabled.
- **Correlation**: `GateTrace` shares the same `DecisionId` as the rejection entry for easy correlation.
- **Performance**: GateTrace construction is lightweight (simple field extraction, no allocations beyond object creation).
- **Backwards Compatibility**: Existing journal readers ignore unknown fields; new readers handle missing `GateTrace` gracefully.

---

## Testing

See [GateTraceSerializationTests.cs](../RamStockAlerts.Tests/GateTraceSerializationTests.cs) for comprehensive serialization tests covering:
- All fields present
- Null optional fields
- Deterministic output
- Schema versioning
- JSON structure validation
- Feature toggle behavior
