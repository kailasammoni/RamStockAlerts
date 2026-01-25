# RamStockAlerts – Complete Codebase Documentation

**Generated:** January 25, 2026  
**Purpose:** Comprehensive technical documentation for sharing with reasoning models and AI assistants

---

## Table of Contents

1. [System Overview](#system-overview)
2. [Product Vision & Goals](#product-vision--goals)
3. [Architecture & Design](#architecture--design)
4. [Core Components](#core-components)
5. [Data Flow & Pipelines](#data-flow--pipelines)
6. [Configuration System](#configuration-system)
7. [Runtime Modes](#runtime-modes)
8. [Key Policies & Constraints](#key-policies--constraints)
9. [Journaling & Observability](#journaling--observability)
10. [Execution System](#execution-system)
11. [Testing Strategy](#testing-strategy)
12. [Development Workflow](#development-workflow)
13. [Known Issues & Roadmap](#known-issues--roadmap)
14. [Technical Decisions & Tradeoffs](#technical-decisions--tradeoffs)

---

## 1. System Overview

### What is RamStockAlerts?

RamStockAlerts is an **order-flow intelligence platform** designed to detect transient liquidity dislocations in equity markets using Level II market depth and tick-by-tick tape data from Interactive Brokers (IBKR). Signals and journaling are enabled by default, generating trade blueprints without executing real orders unless execution is explicitly enabled.

### Key Characteristics

- **Money-touching system**: Production-grade safety, logging, and deterministic replay capabilities
- **Scarcity over frequency**: Targets 3–6 high-quality signals per day, not high-frequency spam
- **Signals-first design**: Validates strategy and gate logic before enabling live execution
- **Deterministic replay**: Records and replays IBKR market data for debugging and backtesting
- **Depth evaluation windows**: Time-boxed market depth analysis with strict slot management

### Technology Stack

- **Language**: C# (.NET 8)
- **Persistence**: SQLite (default) or PostgreSQL
- **Event Store**: File-based JSONL (default) or Postgres-backed
- **Market Data**: Interactive Brokers TWS API
- **Observability**: Serilog (structured logging), optional Application Insights
- **Web Framework**: ASP.NET Core (REST API)

---

## 2. Product Vision & Goals

### System Objective

Detect **transient order-book imbalances** that statistically precede short-term price continuation and deliver **actionable, human-executed trade blueprints** with strict quality control.

### Target Signal Characteristics

| Metric | Requirement |
|--------|-------------|
| Daily Signal Count | 3–6 high-quality signals |
| Signal Score | ≥ 7.5 / 10.0 |
| Win Rate Target | > 60% |
| Risk-to-Reward | Minimum 1:2 (4× spread stop, 8× spread target) |
| Position Size | Risk capped at 0.25% account |

### Core Strategy Elements

1. **Queue Imbalance**: Bid depth / Ask depth ratio (threshold: ≥ 3:1)
2. **Spread Compression**: Tight bid-ask spread (≤ 3 cents)
3. **Tape Velocity**: Trade rate acceleration (≥ 5 trades/sec)
4. **VWAP Reclaim**: Price crossing VWAP with volume surge
5. **Bid Wall Persistence**: Best bid/ask unchanged ≥ 1000ms

### Anti-Pattern Detection (Rejection Rules)

- **Fake bid walls**: Rapid cancellation after placement
- **Ask replenishment**: Refill rate exceeds fill rate (absorption failure)
- **Spread widening**: Post-trigger spread expansion > 50%
- **Tape slowdown**: Trade velocity collapse after signal
- **Same symbol cooldown**: < 10 minutes between signals for same ticker

---

## 3. Architecture & Design

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         RamStockAlerts                          │
│                      (ASP.NET Core Host)                        │
├─────────────────────────────────────────────────────────────────┤
│  Runtime Modes:                                                 │
│  • API Host (default)       • Record Mode (IBKR → JSONL)       │
│  • Replay Mode (JSONL → State)  • Daily Rollup (Report)       │
└─────────────────────────────────────────────────────────────────┘
                              ▲
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
        ▼                     ▼                     ▼
┌──────────────┐    ┌──────────────────┐    ┌──────────────┐
│   Universe   │    │  Market Data     │    │   Signal     │
│   Service    │───▶│  Subscription    │───▶│   Trading    │
│              │    │     Manager      │    │ Coordinator  │
└──────────────┘    └──────────────────┘    └──────────────┘
        │                     │                     │
        │                     ▼                     ▼
        │            ┌──────────────────┐    ┌──────────────┐
        │            │  IBKR Market     │    │  OrderFlow   │
        │            │  Data Client     │    │   Metrics    │
        │            └──────────────────┘    └──────────────┘
        │                     │                     │
        └─────────────────────┼─────────────────────┘
                              ▼
                    ┌──────────────────────┐
                    │  Trade        │
                    │     Journal          │
                    │  (JSONL Event Log)   │
                    └──────────────────────┘
```

### Project Structure

```
src/
├── RamStockAlerts/                     # Main API project
│   ├── Controllers/                    # REST API endpoints
│   │   ├── Api/                       # API DTOs
│   │   │   ├── Admin/                 # Admin DTOs
│   │   │   │   └── DiscordNotificationDtos.cs
│   │   │   └── Execution/             # Execution DTOs
│   │   │       ├── BracketIntentDto.cs
│   │   │       ├── LedgerDto.cs
│   │   │       └── OrderIntentDto.cs
│   │   ├── AdminController.cs         # Admin/diagnostics
│   │   └── ExecutionController.cs     # Order placement API
│   ├── Services/                       # Core business logic
│   │   ├── Signals/                   # Signal processing
│   │   │   ├── ITradeJournal.cs       # Journal interface
│   │   │   ├── SignalCoordinator.cs   # Signals loop
│   │   │   ├── SignalHelpers.cs       # Signal utilities
│   │   │   ├── TradeJournal.cs        # Event journaling
│   │   │   └── TradeJournalHeartbeatService.cs
│   │   ├── Universe/                  # Universe filtering
│   │   │   ├── ContractClassificationServices.cs
│   │   │   ├── DepthEligibilityCache.cs
│   │   │   └── DepthUniverseFilter.cs # Depth eligibility filtering
│   │   ├── DailyRollupReporter.cs     # Daily summary reports
│   │   ├── DiscordDeliveryStatusStore.cs
│   │   ├── DiscordNotificationService.cs # Discord alerts
│   │   ├── DiscordNotificationSettingsStore.cs
│   │   ├── FileBasedJournalRotationService.cs
│   │   ├── IbkrRecorderHostedService.cs # IBKR data recording
│   │   ├── IbkrReplayHostedService.cs   # IBKR data replay
│   │   ├── IbkrRequestIdSource.cs
│   │   ├── IJournalRotationService.cs
│   │   ├── MarketDataSubscriptionManager.cs  # IBKR subscription lifecycle
│   │   ├── OutcomeSummaryStore.cs     # Outcome tracking
│   │   ├── PreviewSignalEmitter.cs
│   │   ├── ScarcityController.cs      # Signal throttling
│   │   ├── SubscriptionDiagnosticsHostedService.cs
│   │   ├── SystemSleepPreventer.cs
│   │   └── TradeOutcomeLabeler.cs     # Win/loss labeling
│   ├── Engine/                         # Strategy & metrics
│   │   ├── OrderFlowMetrics.cs        # Microstructure calculations
│   │   └── OrderFlowSignalValidator.cs # Signal scoring/validation
│   ├── Universe/                       # Symbol universe management
│   │   ├── IUniverseSource.cs         # Universe source interface
│   │   ├── IbkrScannerUniverseSource.cs # IBKR scanner integration
│   │   ├── StaticUniverseSource.cs    # Static symbol list
│   │   └── UniverseService.cs         # Universe orchestration
│   ├── Feeds/                          # Market data clients
│   │   └── IBkrMarketDataClient.cs    # IBKR TWS API wrapper
│   ├── Models/                         # Domain models
│   │   ├── Decisions/                 # Decision models
│   │   ├── Microstructure/            # Market microstructure models
│   │   ├── Notifications/             # Notification models
│   │   ├── OrderBookState.cs          # Level II depth state
│   │   ├── OutcomeSummary.cs          # Trade outcome summary
│   │   ├── PerformanceMetrics.cs      # Performance tracking
│   │   ├── TradeJournalEntry.cs       # Journal schema
│   │   └── TradeOutcome.cs            # Trade outcome model
│   └── Data/                           # Persistence layer
│       └── Engine/                    # (empty, reserved)
└── RamStockAlerts.Execution/          # Execution module
    ├── Contracts/                     # Execution contracts
    │   ├── BracketIntent.cs
    │   ├── Enums.cs
    │   ├── ExecutionOptions.cs
    │   ├── ExecutionRequest.cs
    │   ├── ExecutionResult.cs
    │   ├── OrderIntent.cs
    │   └── RiskDecision.cs
    ├── Interfaces/                    # Broker interfaces
    ├── Reporting/
    │   └── ExecutionDailyReporter.cs  # Execution daily reports
    ├── Services/
    │   ├── BracketTemplateBuilder.cs  # Bracket order builder
    │   ├── ExecutionService.cs        # Order submission orchestration
    │   ├── FakeBrokerClient.cs        # Mock broker (default)
    │   └── IbkrBrokerClient.cs        # IBKR real broker (WIP)
    ├── Risk/
    │   └── RiskManagerV0.cs           # Risk caps & validation
    └── Storage/                       # Execution storage

tests/
├── RamStockAlerts.Tests/              # Unit tests
└── RamStockAlerts.Execution.Tests/    # Execution module tests

lib/
└── ibkr/                              # Vendor IBKR API
    └── CSharpClient/
        └── CSharpAPI.csproj
```

---

## 4. Core Components

### 4.1 Universe Pipeline (Authoritative)

The universe pipeline maintains four hierarchical sets:

| Set | Description | Max Size | Subscription Level |
|-----|-------------|----------|-------------------|
| **CandidateSet** | Raw scanner output | Config: `Universe:IbkrScanner:Rows` (200) | None |
| **EligibleSet** | Classified tradable common stocks (no ETFs/ETNs) | Filtered from CandidateSet | None |
| **ProbeSet** | Level I + optional tape subscriptions | Config: `MarketData:MaxLines` (80) | Level I + Tape |
| **EvalSet** | Temporary depth + tick-by-tick upgrades | Config: 3 slots (hardcoded) | Depth + Tick-by-Tick |

**ActiveUniverse** = EvalSet ∩ ReadyByGates (symbols passing freshness & validity gates)

**Policy:** Strategy **MUST NOT** fire on symbols outside ActiveUniverse.

### 4.2 MarketDataSubscriptionManager

**Responsibilities:**
- Apply universe snapshots from UniverseService
- Manage IBKR subscription lifecycle (L1/depth/tick-by-tick)
- Enforce subscription caps and depth slot limits
- Track depth/tick-by-tick cooldowns
- Emit UniverseUpdate journal entries
- Handle IBKR error 10092 (depth ineligibility)

**Key Methods:**
- `ApplyUniverseAsync(List<string> symbols)`: Diff and apply universe changes
- `UpgradeToEvaluation(string symbol)`: Upgrade probe to depth+tbt
- `DowngradeFromEvaluation(string symbol)`: Cancel depth+tbt, keep probe
- `IsActiveSymbol(string symbol)`: Check if symbol is in ActiveUniverse

**Constraints:**
- Preserves existing L1 request IDs during depth upgrade
- Applies cooldowns after depth/tbt cancellation
- Never subscribes depth without probe-level subscription
- Logs subscription stats every cycle

### 4.3 SignalCoordinator

**Responsibilities:**
- Receive OrderBookState snapshots from IBKR client
- Gate snapshots on ActiveUniverse membership
- Throttle evaluations (250ms per symbol)
- Run OrderFlowMetrics + OrderFlowSignalValidator
- Journal acceptances/rejections with full context
- Apply scarcity controls (max 6 blueprints/day)

**Gate Sequence:**
1. **ActiveUniverse gate**: Drop if not in ActiveUniverse
2. **Evaluation throttle**: Skip if < 250ms since last eval
3. **Book validity gate**: Reject if crossed/empty/stale
4. **Tape freshness gate**: Reject if no recent trades (5s window)
5. **Scoring gate**: Compute signal score, reject if < threshold
6. **Scarcity gate**: Reject if daily quota exceeded

**Journal Emission (SchemaVersion=2):**
- Rejection: Symbol, reason, gate trace (optional), timestamp
- Acceptance: Symbol, score, blueprint, depth snapshot, tape context, gate trace

### 4.4 OrderFlowMetrics

**Computes:**
- **QueueImbalance**: Σ(BidDepth[0..3]) / Σ(AskDepth[0..3])
- **WallPersistence**: Time best level unchanged (≥1000ms)
- **TapeAcceleration**: Δ(trades/sec) over 3-second window
- **SpoofScore**: CancelRate / AddRate (< 0.3 = spoofing)
- **AbsorptionRate**: Fill rate vs refill rate
- **Spread**: Ask - Bid
- **MidPrice**: (Bid + Ask) / 2

**Hard Gate:** Returns zeroed snapshot if book is invalid (crossed, empty, stale).

### 4.5 OrderFlowSignalValidator

**Scoring Weights:**
| Rule | Weight |
|------|--------|
| Spread ≤ 0.03 | 2 |
| Bid-Ask Ratio ≥ 3 | 3 |
| Tape Speed ≥ 5 | 2 |
| VWAP Reclaim | 2 |
| Bid > Ask Size | 1 |
| **Max Score** | **10** |

**Rejection Logic:**
- Spoof detection (rapid cancel rate)
- Ask replenishment (refill > fill)
- Spread widening post-trigger
- Tape velocity collapse
- Absorption failure (no bid absorption)

### 4.6 UniverseService

**Sources:**
- **Static**: Hardcoded symbol list (config: `Universe:StaticTickers`)
- **IbkrScanner**: Live IBKR scanner queries (HOT_BY_VOLUME, configurable filters)

**Flow:**
1. Fetch symbols from configured source (default: IbkrScanner)
2. Apply DepthUniverseFilter to classify contracts (exclude ETFs/ETNs)
3. Cache universe with 5-minute TTL
4. On scanner failure: Fall back to last successful universe
5. Refresh every `Universe:RebuildIntervalHours` (default: 4 hours)

**Scanner Filters (IbkrScanner):**
- Price: $8–$20
- Volume: > 500K
- Float shares: < 150M
- Instrument: STK (stocks)
- Location: STK.US.MAJOR

### 4.7 DiscordNotificationService

**Responsibilities:**
- Send trade blueprints to Discord webhook
- Format rich embeds with score breakdown and blueprint details
- Retry with exponential backoff on failures
- Track delivery status via DiscordDeliveryStatusStore
- Support enable/disable via DiscordNotificationSettingsStore

**Related Files:**
- `Services/DiscordNotificationService.cs` – Core notification logic
- `Services/DiscordDeliveryStatusStore.cs` – Delivery tracking
- `Services/DiscordNotificationSettingsStore.cs` – Settings persistence
- `Controllers/Api/Admin/DiscordNotificationDtos.cs` – Admin API DTOs

### 4.8 TradeOutcomeLabeler

**Responsibilities:**
- Monitor accepted signals for TP/SL hit
- Label outcomes as Win, Loss, or Open
- Store results to OutcomeSummaryStore
- Enable performance tracking and win rate calculation

**Related Files:**
- `Services/TradeOutcomeLabeler.cs` – Outcome labeling logic
- `Services/OutcomeSummaryStore.cs` – Outcome persistence
- `Models/TradeOutcome.cs` – Outcome model
- `Models/OutcomeSummary.cs` – Summary aggregation

---

## 5. Data Flow & Pipelines

### 5.1 Normal API Mode (Signals)

```
1. UniverseService.GetUniverseAsync()
   ├─▶ IbkrScannerUniverseSource (scanner query)
   ├─▶ DepthUniverseFilter (classify contracts)
   └─▶ Cache universe (5-min TTL)

2. MarketDataSubscriptionManager.ApplyUniverseAsync()
   ├─▶ Diff new universe vs current subscriptions
   ├─▶ Subscribe ProbeSet symbols (L1 + tape)
   ├─▶ Upgrade top candidates to EvalSet (depth + tbt)
   ├─▶ Apply slot limits (3 depth slots)
   ├─▶ Emit UniverseUpdate journal entry
   └─▶ Log subscription stats

3. IBkrMarketDataClient receives market data
   ├─▶ OnTickPrice / OnTickSize (L1)
   ├─▶ OnUpdateMktDepth (depth updates)
   ├─▶ OnTickByTick (tape prints)
   └─▶ Build OrderBookState snapshots

4. SignalCoordinator.ProcessSnapshot()
   ├─▶ ActiveUniverse gate (drop if not active)
   ├─▶ Evaluation throttle (250ms)
   ├─▶ OrderFlowMetrics.UpdateMetrics()
   ├─▶ OrderFlowSignalValidator.ValidateSignal()
   ├─▶ Gate checks (validity, freshness, scoring)
   ├─▶ ScarcityController.ShouldAccept()
   └─▶ TradeJournal.WriteAsync() (accept/reject)

5. Trade Blueprint (if accepted)
   ├─▶ Entry: Last Ask
   ├─▶ Stop: Entry - (Spread × 4)
   ├─▶ Target: Entry + (Spread × 8)
   └─▶ Position Size: 0.25% account risk

6. DiscordNotificationService (if enabled)
   ├─▶ Format signal as Discord embed
   ├─▶ Include score breakdown + blueprint
   ├─▶ Retry with exponential backoff
   └─▶ Track delivery status (DiscordDeliveryStatusStore)

7. TradeOutcomeLabeler (post-signal)
   ├─▶ Monitor price vs TP/SL levels
   ├─▶ Label outcome (Win/Loss/Open)
   └─▶ Store to OutcomeSummaryStore
```

### 5.2 Record Mode (IBKR → JSONL)

```
MODE=record
  ├─▶ IbkrRecorderHostedService
  ├─▶ Subscribe to configured symbol (Ibkr:Symbol)
  ├─▶ Request depth (Ibkr:DepthRows) + tick-by-tick
  ├─▶ Write depth/tape events to logs/ibkr-depth-*.jsonl
  └─▶ Write tape events to logs/ibkr-tape-*.jsonl
```

### 5.3 Replay Mode (JSONL → State)

```
MODE=replay
  ├─▶ IbkrReplayHostedService
  ├─▶ Read logs/ibkr-depth-*.jsonl + logs/ibkr-tape-*.jsonl
  ├─▶ Reconstruct OrderBookState snapshots
  ├─▶ Feed to SignalCoordinator
  └─▶ Output to replay-output.txt (deterministic results)
```

---

## 6. Configuration System

### 6.1 Configuration Files

- **appsettings.json**: Production defaults
- **appsettings.Development.json**: Local overrides (not committed)
- **Environment variables**: Override any config key (highest priority)

### 6.2 Key Configuration Sections

#### Execution Flags
```json
{
  "Execution": {
    "Enabled": false,
    "Live": false
  }
}
```

#### IBKR Connection
```json
{
  "IBKR": {
    "Enabled": true,
    "Host": "127.0.0.1",
    "Port": 7496,        // 7496 = TWS live, 7497 = paper, 4001 = Gateway
    "ClientId": 1
  }
}
```

#### Market Data Limits
```json
{
  "MarketData": {
    "MaxLines": 80,              // ProbeSet capacity
    "EnableDepth": true,
    "EnableTape": true,
    "DepthRows": 5,              // IBKR depth levels
    "TickByTickMaxSymbols": 6,   // Tick-by-tick slot cap
    "MinHoldMinutes": 5
  }
}
```

#### Universe Configuration
```json
{
  "Universe": {
    "Source": "IbkrScanner",     // Static or IbkrScanner
    "StaticTickers": ["AAPL", "MSFT"],
    "IbkrScanner": {
      "Instrument": "STK",
      "LocationCode": "STK.US.MAJOR",
      "ScanCode": "HOT_BY_VOLUME",
      "Rows": 200,
      "AbovePrice": 8,
      "BelowPrice": 20,
      "AboveVolume": 500000,
      "FloatSharesBelow": 150000000
    }
  }
}
```

#### Signals Journal
```json
{
  "SignalsJournal": {
    "FilePath": "logs/trade-journal.jsonl",
    "EmitGateTrace": true        // Include gate diagnostic snapshots
  }
}
```

#### Scarcity Controls
```json
{
  "Scarcity": {
    "MaxBlueprintsPerDay": 6,
    "MaxPerSymbolPerDay": 1,
    "GlobalCooldownMinutes": 45,
    "SymbolCooldownMinutes": 999  // Effectively once per day
  }
}
```

#### Execution Module
```json
{
  "Execution": {
    "Enabled": false,            // Disabled by default
    "Broker": "Fake",            // Fake (mock) or IBKR (not implemented)
    "MaxNotionalUsd": 2000,
    "MaxShares": 500
  }
}
```

#### Persistence
```json
{
  "UsePostgreSQL": false,        // false = SQLite, true = Postgres
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=ramstockalerts.db",
    "PostgreSQL": "Host=localhost;Database=ramstockalerts;..."
  }
}
```

---

## 7. Runtime Modes

### 7.1 API Host (Default)

**Trigger:** No `MODE` env var or `MODE` not set to record/replay.

**Behavior:**
- Starts ASP.NET Core web host
- Runs UniverseService + MarketDataSubscriptionManager
- Connects to IBKR TWS
- Runs signal loop
- Exposes REST API endpoints

**Endpoints:**
- `GET /api/signals` – Recent trade journal entries
- `POST /api/execution/order` – Place single order (requires Execution:Enabled)
- `POST /api/execution/bracket` – Place bracket order
- `GET /api/execution/ledger` – Execution history
- `GET /api/admin/health` – System health

### 7.2 Record Mode

**Trigger:** `MODE=record`

**Behavior:**
- Starts IbkrRecorderHostedService
- Subscribes to single symbol (config: `Ibkr:Symbol`)
- Requests depth + tick-by-tick
- Writes raw events to:
  - `logs/ibkr-depth-YYYYMMDD-HHmmss.jsonl`
  - `logs/ibkr-tape-YYYYMMDD-HHmmss.jsonl`
- Does NOT run universe pipeline or signal loop
- Runs until terminated (Ctrl+C)

**Use Case:** Capture real IBKR data for replay/debugging.

### 7.3 Replay Mode

**Trigger:** `MODE=replay`

**Behavior:**
- Starts IbkrReplayHostedService
- Reads depth + tape JSONL files from logs/
- Reconstructs OrderBookState snapshots deterministically
- Feeds snapshots to SignalCoordinator
- Outputs evaluation results to `replay-output.txt`
- Does NOT connect to IBKR or emit real journal entries

**Use Case:** Deterministic debugging of strategy logic without live market.

### 7.4 Daily Rollup (Report Mode)

**Trigger:** `Report:DailyRollup=true`

**Behavior:**
- Reads trade journal (config: `Report:JournalPath`)
- Aggregates metrics (win rate, avg score, rejections by reason)
- Prints to console or writes to file (config: `Report:WriteToFile`)
- Exits after completion

**Use Case:** Periodic performance reporting / backtesting analysis.

---

## 8. Key Policies & Constraints

### 8.1 Authoritative System Policies

**Source:** [Authoritative System Policy.md](Authoritative%20System%20Policy.md)

1. **Four System Sets (must be explicit in logs)**
   - CandidateSet → EligibleSet → ProbeSet → EvalSet
   - ActiveUniverse = EvalSet ∩ ReadyByGates

2. **Hard Constraints**
   - EvalSlots = 3 (fixed)
   - Depth subscriptions are temporary (evaluation windows only)
   - Strategy MUST NOT execute on non-evaluation symbols
   - Tape freshness gating MUST use local receipt time (`lastTapeRecvMs`)

3. **Time Sources**
   - `lastTapeRecvMs`: Local receipt timestamp (authoritative for gating)
   - `lastTapeEventMs`: Exchange timestamp (analytics only)
   - `skewMs`: `lastTapeRecvMs - lastTapeEventMs`

4. **Evaluation Window Policy**
   - Entry: Symbol must be in ProbeSet, upgrade preserves L1 requestId
   - Exit: Signal emitted OR timeout OR data invalid
   - On exit: Cancel depth + tbt, apply cooldown, keep probe unless dropped

5. **Signal Emission (anti-spam)**
   - Rising-edge triggers (score crosses threshold, not continuous)
   - Evaluation throttle (250ms per symbol)
   - Cooldowns are secondary protection

6. **Required Logs/Metrics (every cycle)**
   - CandidateSet size
   - EligibleSet size
   - ProbeSet size
   - EvalSet symbols
   - ActiveUniverse symbols
   - Evaluation outcomes (duration, exit reason)

### 8.2 Depth Evaluation Window Policy

**Source:** [Depth Evaluation Window Policy.md](%23%20RamStockAlerts%20%E2%80%93%20Depth%20Evaluation%20Wind.md)

**Entry (Upgrade):**
- Symbol is already Probe
- DepthSlots available
- Classification complete
- Not in cooldown

**During Evaluation:**
- Depth + tape ingestion
- Strategy + gating logic runs
- Bounded duration (60–180s configurable)

**Exit (Mandatory on any):**
- Signal emitted (accept/reject)
- Evaluation timeout
- Data invalid (stale tape, broken book)
- Manual abort

**On Exit:**
- Cancel depth subscription
- Cancel tick-by-tick
- Record evaluation outcome (journal)
- Start cooldown timer (default: 1 day)

**Forbidden States (System Bugs):**
- Depth active without evaluation timer
- Evaluation exceeding max duration
- More than 3 active evaluations
- Strategy firing on non-evaluation symbols

### 8.3 Tape Freshness Policy

**Authoritative:** Local receipt time (`lastTapeRecvMs`), not exchange time.

**Default Config:**
- Warmup: 5 trades in 10-second window
- Staleness: No trade in last 5 seconds
- Gate: `nowMs - lastTapeRecvMs <= 5000`

**Log Fields (every tick):**
- `lastTapeRecvMs`
- `lastTapeEventMs`
- `skewMs` (latency indicator)
- `tradesInWarmupWindow`

---

## 9. Journaling & Observability

### 9.1 Trade Journal

**Format:** JSONL (JSON Lines), one entry per line  
**Location:** `logs/trade-journal.jsonl`  
**SchemaVersion:** 2 (current)

**Entry Types:**

#### UniverseUpdate
Emitted once per `ApplyUniverseAsync` cycle.

**Fields:**
- `EntryType`: "UniverseUpdate"
- `SchemaVersion`: 2
- `SessionId`: Guid
- `MarketTimestampUtc`: Timestamp of refresh
- `UniverseUpdate.SchemaVersion`: 1
- `UniverseUpdate.Candidates`: Top 20 symbols (limited to prevent spam)
- `UniverseUpdate.ActiveUniverse`: ≤ 3 symbols in EvalSet passing gates
- `UniverseUpdate.Exclusions`: Symbols excluded from ActiveUniverse + reason
- `UniverseUpdate.Counts`: Verification counts (candidates, active, depth, tbt, tape)

#### Rejection
Emitted when a signal is rejected by gates or scoring.

**Fields:**
- `EntryType`: "Rejection"
- `SchemaVersion`: 2
- `DecisionId`: Guid
- `Symbol`: Ticker
- `RejectionReason`: String (e.g., "NotReady_TapeStale", "Score_BelowThreshold")
- `MarketTimestampUtc`: Timestamp of rejection
- `GateTrace` (optional): Diagnostic snapshot (tape age, depth age, config values)

**Common RejectionReasons:**
- `NotReady_TapeNotWarmedUp`
- `NotReady_TapeStale`
- `NotReady_NoDepth`
- `NotReady_DepthStale`
- `BookInvalid_Crossed`
- `BookInvalid_Empty`
- `Score_BelowThreshold`
- `Scarcity_DailyQuotaReached`
- `Scarcity_SymbolCooldown`

#### Acceptance
Emitted when a signal passes all gates and scoring.

**Fields:**
- `EntryType`: "Acceptance"
- `SchemaVersion`: 2
- `DecisionId`: Guid
- `Symbol`: Ticker
- `Score`: Decimal (0.0–10.0)
- `Blueprint`: TradeBlueprint (entry, stop, target, position size)
- `DepthSnapshot`: Level II book state (bids/asks)
- `TapeContext`: Recent tape prints
- `DepthDeltas`: Size changes at best levels
- `GateTrace` (optional): Diagnostic snapshot
- `MarketTimestampUtc`: Timestamp of acceptance

### 9.2 GateTrace Schema

**Purpose:** Diagnostic context for rejections.

**Fields:**
- `SchemaVersion`: 1
- `NowMs`: Unix timestamp of gate check
- `LastTradeMs`: Timestamp of last trade (null if none)
- `TradesInWarmupWindow`: Count of trades in warmup window
- `WarmedUp`: Boolean (warmup criteria met)
- `StaleAgeMs`: Age of last trade in ms (null if none)
- `LastDepthMs`: Timestamp of last depth update (null if none)
- `DepthAgeMs`: Age of last depth update in ms
- `DepthRowsKnown`: Number of depth levels (max of bids/asks)
- `DepthSupported`: Boolean (depth subscription enabled)
- `WarmupMinTrades`: Config value
- `WarmupWindowMs`: Config value
- `StaleWindowMs`: Config value
- `DepthStaleWindowMs`: Config value

### 9.3 Logging Strategy

**Serilog Configuration:**
- Console: Structured output, Warning level for Microsoft logs
- File: Rolling daily logs (`logs/ramstockalerts-YYYYMMDD.txt`)
- Format: `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}`

**Log Levels:**
- **Debug**: Internal state transitions, subscription events
- **Information**: Universe refreshes, evaluations, signal decisions
- **Warning**: Tape staleness, depth errors, gate rejections
- **Error**: IBKR connection failures, data corruption, unhandled exceptions

**Structured Properties:**
- `Symbol`
- `RequestId`
- `EvalSlots`
- `DepthSlots`
- `ActiveUniverse`
- `RejectionReason`
- `Score`

---

## 10. Execution System

### 10.1 Architecture

**Module:** `RamStockAlerts.Execution`  
**Default State:** Disabled (`Execution:Enabled=false`)

**Components:**
- **ExecutionService**: Orchestration layer, risk validation
- **IBrokerClient**: Interface for broker implementations
- **FakeBrokerClient**: Mock broker (always succeeds, generates fake order IDs)
- **IbkrBrokerClient**: IBKR broker (not implemented, falls back to Fake)
- **RiskManagerV0**: Pre-trade risk checks
- **ExecutionLedger**: In-memory execution history storage

### 10.2 API Endpoints

#### POST /api/execution/order
Place a single order.

**Request:**
```json
{
  "symbol": "AAPL",
  "side": "Buy",
  "type": "Market",
  "quantity": 100,
  "tif": "Day"
}
```

**Response (200):**
```json
{
  "status": "Accepted",
  "rejectionReason": null,
  "brokerName": "FakeBroker",
  "brokerOrderIds": ["FAKE-abc123..."],
  "timestampUtc": "2026-01-16T12:34:56.789Z",
  "debug": "Fake order placed: AAPL Buy 100"
}
```

#### POST /api/execution/bracket
Place bracket order (entry + stop + take-profit).

**Request:**
```json
{
  "entry": { "symbol": "TSLA", "side": "Buy", "type": "Limit", "quantity": 50, "limitPrice": 200.00 },
  "stopLoss": { "symbol": "TSLA", "side": "Sell", "type": "Stop", "quantity": 50, "stopPrice": 190.00 },
  "takeProfit": { "symbol": "TSLA", "side": "Sell", "type": "Limit", "quantity": 50, "limitPrice": 210.00 }
}
```

**Response (200):**
```json
{
  "status": "Accepted",
  "rejectionReason": null,
  "brokerName": "FakeBroker",
  "brokerOrderIds": ["FAKE-ENTRY-abc", "FAKE-STOP-def", "FAKE-PROFIT-ghi"],
  "timestampUtc": "2026-01-16T12:35:01.234Z",
  "debug": "Fake bracket placed: TSLA (Entry + Stop + TP)"
}
```

#### GET /api/execution/ledger
Query execution history.

**Response (200):**
```json
{
  "intents": [ ... ],
  "brackets": [ ... ],
  "fills": [ ... ]
}
```

### 10.3 Risk Constraints (RiskManagerV0)

**Pre-Trade Checks:**
- Max notional per trade: `Execution:MaxNotionalUsd` (default: $2000)
- Max shares per trade: `Execution:MaxShares` (default: 500)
- Live execution (`Execution:Live=true`) MUST include stop-loss in bracket orders

**Risk Rejection Reasons:**
- `NotionalTooHigh`
- `QuantityTooHigh`
- `MissingStopLoss_LiveMode`

### 10.4 Broker Implementations

**FakeBrokerClient (default):**
- Always returns success
- Generates fake order IDs: `FAKE-<guid>`
- No real execution
- Used for testing and non-live validation

**IbkrBrokerClient (not implemented):**
- Placeholder for real IBKR order placement
- Currently falls back to FakeBrokerClient
- Future implementation: `placeOrder()` via IBKR TWS API

---

## 11. Testing Strategy

### 11.1 Test Projects

**RamStockAlerts.Tests:**
- Unit tests for core logic
- Strategy/gating tests
- Universe filter tests
- Market data parsing tests

**RamStockAlerts.Execution.Tests:**
- Execution module tests
- Risk validation tests
- Bracket template builder tests
- Fake broker tests

### 11.2 Key Test Suites

**ActiveUniverseTests.cs:**
- Verifies ActiveUniverse gate logic
- Tests symbol filtering based on depth/tape/tbt status

**DepthDeltaTrackerTests.cs:**
- Tests depth delta computation
- Validates size change tracking at best levels

**AbsorptionRejectTests.cs:**
- Tests absorption detection logic
- Validates ask replenishment vs fill rate

**RiskManagerV0Tests.cs:**
- Risk validation tests
- Bracket order validation (stop-loss requirement)

**BracketTemplateBuilderTests.cs:**
- Blueprint generation tests
- Stop/target calculation validation

### 11.3 Test Command

```bash
dotnet test RamStockAlerts.sln
```

---

## 12. Development Workflow

### 12.1 Building the Project

**Full Solution:**
```bash
dotnet build RamStockAlerts.sln -c Debug
```

**Single Project:**
```bash
dotnet build src/RamStockAlerts/RamStockAlerts.csproj -c Debug
```

**Available Task:** `Build RamStockAlerts.sln Debug`

### 12.2 Running Locally

**API Host (signals enabled by default):**
```bash
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**With environment overrides:**
```bash
$env:MarketData:MaxLines = 100
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Record Mode:**
```bash
$env:MODE = "record"
$env:SYMBOL = "AAPL"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Replay Mode:**
```bash
$env:MODE = "replay"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Daily Rollup:**
```bash
$env:Report:DailyRollup = "true"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

### 12.3 Code Change Pattern (Safety Rules)

**From Global rules.instructions.md:**

1. **Smallest correct change**: No unrelated refactors
2. **Add observability**: Logs + journal entries for new behavior
3. **Avoid magic defaults**: Explicit config, log when defaulting
4. **No unrelated refactors**: Stay focused on the task
5. **Guarded by config**: New features behind feature flags
6. **Deterministic tests**: Unit tests for new logic
7. **Schema versioning**: Bump SchemaVersion when changing journal shape

**Pre-Merge Safety Checks:**
- Depth slots ≤ configured max (3)
- ActiveUniverse gate enforced
- Tape staleness gates on receipt time (`lastTapeRecvMs`)
- Journal SchemaVersion correct
- Evaluation windows start/stop logged
- Execution endpoints respect `Execution:Enabled`
- Risk limits enforced

### 12.4 Debugging with Replay

1. Enable recorder mode with target symbol:
   ```bash
   $env:MODE = "record"
   $env:SYMBOL = "TSLA"
   dotnet run
   ```

2. Let it run for desired duration (captures depth + tape to JSONL)

3. Stop recorder (Ctrl+C)

4. Run replay mode:
   ```bash
   $env:MODE = "replay"
   dotnet run
   ```

5. Review `replay-output.txt` for deterministic evaluation results

---

## 13. Known Issues & Roadmap

### 13.1 Known Issues (In Scope)

**From ENGINEERING_STATE.md:**

✅ **VERIFIED:**
- `reqMktDepth` receiving multi-level depth
- Size updates without price changes observed
- Tape prints streaming correctly
- Replay mechanism exists

⚠️ **KNOWN ISSUES:**
- Crossed book observed during replay (root cause: depth insert/delete semantics)
- Some replay-time exceptions (exception containment needed)

🔧 **FIXES IN PROGRESS:**
- Ordered-list depth handling
- Book reset detection
- Book validity gate (partially implemented)
- Exception containment

### 13.2 Out of Scope (DO NOT TOUCH)

**From ENGINEERING_STATE.md:**
- Scoring thresholds (currently tuned, avoid changes)
- Signal frequency tuning (managed by scarcity controller)
- Alerting / Discord (disabled in production config)
- Universe expansion (capped by IBKR limits)

### 13.3 Roadmap / Next Steps

**Phase 1: Stability (Current Focus)**
- ✅ Depth evaluation windows implemented
- ✅ ActiveUniverse gate enforced
- ✅ Tape freshness gating (receipt time)
- ✅ GateTrace schema (v1)
- ✅ UniverseUpdate journal entries
- 🔧 Book validity edge cases (crossed book handling)
- 🔧 IBKR error 10092 mitigation (depth eligibility cache)

**Phase 2: Execution Module**
- ✅ ExecutionController API
- ✅ FakeBrokerClient (mock orders)
- ✅ RiskManagerV0 (risk caps)
- 🔜 IbkrBrokerClient (real order placement)
- 🔜 Order state tracking
- 🔜 Fill monitoring
- 🔜 Position tracking

**Phase 3: Performance & Observability**
- 🔜 Application Insights integration
- 🔜 Prometheus metrics export
- 🔜 Real-time dashboard (Grafana)
- 🔜 Alert webhooks (Discord/Slack/SMS)
- 🔜 Performance profiling (evaluations/sec)

**Phase 4: Advanced Features**
- 🔜 Multi-symbol portfolio tracking
- 🔜 Dynamic stop-loss adjustment
- 🔜 Partial fill handling
- 🔜 Market regime detection
- 🔜 Adaptive thresholds (ML-based tuning)

---

## 14. Technical Decisions & Tradeoffs

### 14.1 Why Signals-First?

**Decision:** Signals and journaling are always on; execution remains disabled by default.

**Rationale:**
- Money-touching systems require validation before live execution
- Signals-first allows strategy validation with zero risk
- Journal provides audit trail for backtesting and compliance
- Easier to iterate on strategy logic without broker integration overhead

**Tradeoff:** Delays live trading capability, but reduces catastrophic risk.

### 14.2 Why JSONL for Journal?

**Decision:** File-based JSONL for Trade journal and event store (default).

**Rationale:**
- Human-readable, line-oriented (no partial writes)
- Append-only (crash-safe)
- No database dependency for core functionality
- Easy to parse, replay, and analyze with standard tools (jq, Python)
- Versioned schemas (SchemaVersion field) allow evolution

**Tradeoff:** Not queryable like SQL; requires parsing for analysis. Mitigated by optional Postgres event store.

### 14.3 Why 3 Depth Slots?

**Decision:** Hard limit of 3 concurrent depth subscriptions (EvalSlots = 3).

**Rationale:**
- IBKR has strict market data line limits (default: 100 total, ~80 usable)
- Depth is expensive (5 levels × 2 sides = 10 updates per tick)
- Tick-by-tick is expensive (every trade = 1 update)
- Quality over quantity (scarcity controller targets 3–6 signals/day)
- Focus rotation allows cycling through candidates without exceeding caps

**Tradeoff:** Can't evaluate all candidates simultaneously. Mitigated by ProbeSet (80 symbols with L1/tape) for monitoring.

### 14.4 Why Fake Broker Default?

**Decision:** `Execution:Broker=Fake` and `Execution:Enabled=false` by default.

**Rationale:**
- Signals-first requires no real broker
- Fake broker enables execution API testing without IBKR account
- IBKR broker implementation incomplete (WIP)
- Safer default for development/staging environments

**Tradeoff:** Can't place real orders. Mitigated by clear config flag (`Execution:Enabled`).

### 14.5 Why Local Receipt Time for Tape Freshness?

**Decision:** Gate on `lastTapeRecvMs` (local receipt) instead of `lastTapeEventMs` (exchange time).

**Rationale:**
- Network latency and IBKR feed delays are external risks
- Exchange timestamps may be skewed or delayed
- Receipt time measures end-to-end freshness (exchange → IBKR → us)
- Prevents stale data from passing gates due to clock skew

**Tradeoff:** May reject fresh data if network is slow. Mitigated by configurable staleness window (default 5s).

### 14.6 Why IbkrScanner Over Static Universe?

**Decision:** Default to `Universe:Source=IbkrScanner` instead of static tickers.

**Rationale:**
- Markets change daily (volume, volatility, liquidity)
- Scanner adapts to current hot sectors and momentum stocks
- Static lists become stale quickly
- Scanner filters (price, volume, float) enforce strategy requirements

**Tradeoff:** Scanner failures require fallback to cached universe. Mitigated by retry logic and last-successful-universe caching.

### 14.7 Why SQLite Default?

**Decision:** Default to SQLite (`UsePostgreSQL=false`).

**Rationale:**
- Zero-config persistence (no DB server required)
- Sufficient for single-instance deployment
- Lower operational complexity (no Postgres maintenance)
- File-based (easy backups, migrations)

**Tradeoff:** No horizontal scaling, less robust concurrency. Mitigated by optional Postgres support for production.

---

## Appendix A: File Locations

**Configuration:**
- [src/RamStockAlerts/appsettings.json](src/RamStockAlerts/appsettings.json)
- [src/RamStockAlerts/appsettings.Development.json](src/RamStockAlerts/appsettings.Development.json)

**Documentation:**
- [ProductGoal.md](ProductGoal.md) – Product vision and requirements
- [Authoritative System Policy.md](Authoritative%20System%20Policy.md) – Core system constraints
- [Depth Evaluation Window Policy.md](%23%20RamStockAlerts%20%E2%80%93%20Depth%20Evaluation%20Wind.md) – Depth subscription lifecycle
- [ENGINEERING_STATE.md](ENGINEERING_STATE.md) – Current implementation state
- [README/EXECUTION_API.md](README/EXECUTION_API.md) – Execution API documentation
- [README/UniverseUpdateJournalEntry.md](README/UniverseUpdateJournalEntry.md) – Journal schema docs
- [README/GateTrace_Schema.md](README/GateTrace_Schema.md) – GateTrace schema docs
- [docs/Architecture.md](docs/Architecture.md) – High-level architecture overview
- [docs/DataContracts.md](docs/DataContracts.md) – Schema definitions
- [docs/DecisionLog.md](docs/DecisionLog.md) – Change history
- [docs/IBKR_UPGRADE.md](docs/IBKR_UPGRADE.md) – TWS API upgrade guide
- [SKILLS.md](SKILLS.md) – Repo skills index

**Core Source Files:**
- [src/RamStockAlerts/Program.cs](src/RamStockAlerts/Program.cs) – Application entry point
- [src/RamStockAlerts/Services/Signals/SignalCoordinator.cs](src/RamStockAlerts/Services/Signals/SignalCoordinator.cs) – Signals loop
- [src/RamStockAlerts/Services/Signals/TradeJournal.cs](src/RamStockAlerts/Services/Signals/TradeJournal.cs) – Event journaling
- [src/RamStockAlerts/Services/MarketDataSubscriptionManager.cs](src/RamStockAlerts/Services/MarketDataSubscriptionManager.cs) – Subscription lifecycle
- [src/RamStockAlerts/Services/DiscordNotificationService.cs](src/RamStockAlerts/Services/DiscordNotificationService.cs) – Discord alerts
- [src/RamStockAlerts/Services/TradeOutcomeLabeler.cs](src/RamStockAlerts/Services/TradeOutcomeLabeler.cs) – Win/loss labeling
- [src/RamStockAlerts/Services/Universe/DepthUniverseFilter.cs](src/RamStockAlerts/Services/Universe/DepthUniverseFilter.cs) – Depth eligibility filtering
- [src/RamStockAlerts/Universe/UniverseService.cs](src/RamStockAlerts/Universe/UniverseService.cs) – Universe orchestration
- [src/RamStockAlerts/Engine/OrderFlowMetrics.cs](src/RamStockAlerts/Engine/OrderFlowMetrics.cs) – Microstructure metrics
- [src/RamStockAlerts/Engine/OrderFlowSignalValidator.cs](src/RamStockAlerts/Engine/OrderFlowSignalValidator.cs) – Signal scoring
- [src/RamStockAlerts/Feeds/IBkrMarketDataClient.cs](src/RamStockAlerts/Feeds/IBkrMarketDataClient.cs) – IBKR TWS client
- [src/RamStockAlerts/Models/TradeJournalEntry.cs](src/RamStockAlerts/Models/TradeJournalEntry.cs) – Journal schema
- [src/RamStockAlerts.Execution/Services/ExecutionService.cs](src/RamStockAlerts.Execution/Services/ExecutionService.cs) – Execution orchestration
- [src/RamStockAlerts.Execution/Services/BracketTemplateBuilder.cs](src/RamStockAlerts.Execution/Services/BracketTemplateBuilder.cs) – Bracket order builder

**Logs & Journals:**
- `logs/ramstockalerts-YYYYMMDD.txt` – Application logs
- `logs/trade-journal.jsonl` – Trade journal
- `logs/ibkr-depth-*.jsonl` – Recorded depth data (record mode)
- `logs/ibkr-tape-*.jsonl` – Recorded tape data (record mode)
- `replay-output.txt` – Replay mode output

**Database:**
- `ramstockalerts.db` – SQLite database (default)
- `events.jsonl` – File-based event store (default)

---

## Appendix B: Common Commands

**Build:**
```bash
dotnet build RamStockAlerts.sln -c Debug
```

**Test:**
```bash
dotnet test RamStockAlerts.sln
```

**Run API (signals enabled):**
```bash
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Record IBKR data:**
```bash
$env:MODE = "record"
$env:SYMBOL = "AAPL"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Replay recorded data:**
```bash
$env:MODE = "replay"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Generate daily rollup report:**
```bash
$env:Report:DailyRollup = "true"
dotnet run --project src/RamStockAlerts/RamStockAlerts.csproj
```

**Enable execution module (with fake broker):**
```json
{
  "Execution": {
    "Enabled": true,
    "Broker": "Fake"
  }
}
```

**Query signals:**
```bash
curl http://localhost:5000/api/signals
```

**Place order:**
```bash
curl -X POST http://localhost:5000/api/execution/order \
  -H "Content-Type: application/json" \
  -d '{"symbol":"AAPL","side":"Buy","type":"Market","quantity":100,"tif":"Day"}'
```

---

## Appendix C: Glossary

**ActiveUniverse:** Subset of EvalSet that passes all gates (validity, freshness). Only these symbols may fire strategy.

**Blueprint:** Trade plan with entry, stop-loss, take-profit, and position size.

**CandidateSet:** Raw scanner output before classification filtering.

**Cooldown:** Lockout period after depth/tbt cancellation before re-upgrade.

**DepthSlots:** Maximum concurrent depth subscriptions (default: 3).

**EligibleSet:** CandidateSet filtered to tradable common stocks (excludes ETFs/ETNs).

**EvalSet:** Symbols upgraded to depth + tick-by-tick (max 3).

**GateTrace:** Diagnostic snapshot emitted with rejections (tape age, depth age, config).

**Journaling:** Append-only JSONL event log (trade-journal.jsonl).

**L1 / Level I:** Top-of-book market data (best bid/ask).

**L2 / Level II / Depth:** Multi-level order book (5 levels of bids/asks).

**ProbeSet:** Symbols subscribed to Level I + tape (max 80).

**SchemaVersion:** Versioning field in journal entries (current: 2).

**Signals:** Generate trade blueprints without real execution.

**Tape / Tick-by-Tick:** Time-and-sales data (every trade print).

**UniverseUpdate:** Journal entry type for universe refresh events.

---

**End of Documentation**

For questions, issues, or contributions, refer to the source files and policies linked throughout this document.


