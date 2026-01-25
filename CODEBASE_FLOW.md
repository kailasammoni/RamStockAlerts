# RamStockAlerts – Core System Flows

This document outlines the high-level sequence diagrams for the primary operations of the RamStockAlerts platform.

## 1. Universe Pipeline & Subscription Management

This flow describes how the system discovers candidates and manages IBKR market data subscriptions.

```mermaid
sequenceDiagram
    participant US as UniverseService
    participant Source as IbkrScannerSource / StaticSource
    participant Filter as DepthUniverseFilter
    participant MDM as MarketDataSubscriptionManager
    participant IBKR as IBkrMarketDataClient

    US->>US: GetUniverseAsync() (Every 5 min)
    US->>Source: Fetch candidates
    Source-->>US: Raw Tickers
    US->>Filter: FilterAsync(Raw Tickers)
    Filter-->>US: Eligible Common Stocks
    US->>MDM: ApplyUniverseAsync(EligibleSet)

    MDM->>MDM: Diff (New vs Current)
    MDM->>IBKR: Subscribe L1 + Tape (ProbeSet, max 80)
    MDM->>IBKR: Upgrade to Depth + TBT (EvalSet, max 3)
    MDM->>IBKR: Downgrade/Cancel (if removed)
    MDM->>Journal: Write UniverseUpdate Entry
```

## 2. Signal Loop (Real-Time Analysis)

This flow describes how market data is processed to detect signals and journal them.

```mermaid
sequenceDiagram
    participant IBKR as IBkrMarketDataClient
    participant OBS as OrderBookState
    participant STC as ShadowTradingCoordinator
    participant OFM as OrderFlowMetrics
    participant OFV as OrderFlowSignalValidator
    participant SC as ScarcityController
    participant Journal as ShadowTradeJournal

    IBKR->>OBS: Update Depth / Trade Print
    OBS->>STC: ProcessSnapshot(book)
    STC->>STC: ActiveUniverse Gate
    STC->>STC: Evaluation Throttle (250ms)

    STC->>OFM: GetLatestSnapshot(symbol)
    OFM-->>STC: Metrics (QI, Wall Age, etc.)

    STC->>OFV: EvaluateDecision(book)
    OFV-->>STC: Decision (Accepted/Rejected)

    alt is Accepted
        STC->>STC: Anti-Spoof / Replenishment Gates
        STC->>STC: Build Trade Blueprint
        STC->>SC: StageCandidate(rankScore)
        Note over SC: Wait for Ranking Window
        SC-->>STC: FlushRankedDecisions()
        STC->>Journal: Write Acceptance Entry
    else is Rejected
        STC->>Journal: Write Rejection Entry
    end
```

## 3. Order Execution Flow

This flow describes how manual or automated orders are processed through the execution module.

```mermaid
sequenceDiagram
    participant User as API Client / Controller
    participant ES as ExecutionService
    participant RM as RiskManagerV0
    participant Broker as FakeBrokerClient / IbkrBrokerClient
    participant Ledger as InMemoryExecutionLedger

    User->>ES: PlaceBracketOrder(intent)
    ES->>RM: ValidateRisk(intent)

    alt Risk Passed
        RM-->>ES: Success
        ES->>Broker: SubmitOrders(Entry, Stop, TP)
        Broker-->>ES: BrokerOrderIds
        ES->>Ledger: RecordExecution(intent, result)
        ES-->>User: Accepted (IDs)
    else Risk Failed
        RM-->>ES: Rejected (Reason)
        ES-->>User: Rejected (Reason)
    end
```

## 4. Record & Replay Modes

### Record Mode
```mermaid
sequenceDiagram
    participant Recorder as IbkrRecorderHostedService
    participant IBKR as IBkrMarketDataClient
    participant File as JSONL Log Files

    Recorder->>IBKR: Subscribe Depth + TBT (Target Symbol)
    IBKR->>Recorder: Raw Events
    Recorder->>File: Write Line (Depth/Tape)
```

### Replay Mode
```mermaid
sequenceDiagram
    participant Replayer as IbkrReplayHostedService
    participant File as JSONL Log Files
    participant OBS as OrderBookState
    participant STC as ShadowTradingCoordinator

    Replayer->>File: Read Events
    Replayer->>OBS: Reconstruct State
    Replayer->>STC: ProcessSnapshot(book)
    STC->>Journal: Output to replay-output.txt

## 5. Daily Rollup & Outcome Pipeline

This flow describes how the journal is processed to aggregate metrics and label trade outcomes.

```mermaid
sequenceDiagram
    participant CLI as Program (MODE=report)
    participant DRR as DailyRollupReporter
    participant RS as RollupStats
    participant OTL as TradeOutcomeLabeler
    participant Store as FileOutcomeSummaryStore
    participant Journal as ShadowTradeJournal (JSONL)

    CLI->>DRR: RunAsync(journalPath)
    DRR->>Journal: Read Entries
    Journal-->>DRR: Entries (Signal/UniverseUpdate)
    loop For each Entry
        DRR->>RS: Record(entry)
    end

    DRR->>OTL: LabelOutcomesAsync(Accepted Entries)
    OTL-->>DRR: TradeOutcomes (Win/Loss/Open)

    DRR->>Store: AppendOutcomesAsync(outcomes)
    DRR->>RS: RecordOutcome(outcome)

    DRR->>RS: Render()
    RS-->>CLI: Final Report (Text/File)
```

## 6. Preview Signal Flow

This flow describes how "Preview" mode provides immediate Discord alerts for high-confidence setups without the full gating constraints.

```mermaid
sequenceDiagram
    participant IBKR as IBkrMarketDataClient
    participant OBS as OrderBookState
    participant PSE as PreviewSignalEmitter
    participant OFV as OrderFlowSignalValidator
    participant Discord as Discord Webhook

    IBKR->>OBS: Update Depth / Trade Print
    OBS->>PSE: ProcessSnapshotAsync(book)
    PSE->>PSE: Validity Gates (Book/Tape)

    PSE->>OFV: EvaluateDecision(book)
    OFV-->>PSE: Decision (Accepted/Score)

    alt Score >= MinScore
        PSE->>PSE: Rate Limit Check
        PSE->>Discord: Send Webhook (Embed)
    end
```
```
# RamStockAlerts – Local Runbook (Windows)

This document describes how to run RamStockAlerts continuously on Windows
while allowing active development, branching, and parallel builds.

## Core Principle

Never run the app from the working git directory.

Always run from a published output folder to avoid:
- file locks
- accidental binary overwrites
- branch switches affecting a running process

---

## Folder Layout

C:\workspace\RamStockAlerts\        # Git working directory (dev only)
C:\run\RamStockAlerts\signals\      # Stable running instance
C:\run\RamStockAlerts\dev\          # Test / next build (optional)

---

## Build & Run (Stable Instance)

### Publish Release Build
Creates a self-contained output folder that will not change while running.

```powershell
dotnet publish -c Release -o C:\run\RamStockAlerts\signals

$env:Report__ExecutionDailyRollup="true"
