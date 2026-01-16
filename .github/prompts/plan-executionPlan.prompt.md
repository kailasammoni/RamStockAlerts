# RamStockAlerts – Execution Plan

**Start Date**: January 16, 2026  
**Total Effort**: 12–17 days  
**Goal**: Complete Phases 1-5 with measurable profitability metrics

---

## QUICK START: Task Checklist

### PHASE 1: Outcome Labeling (1–2 days) [CRITICAL PATH START]
**Status**: ⚠️ 20% Complete | **Blocker**: None

- [ ] **1.1** Create `Services/TradeOutcomeLabeler.cs`
  - [ ] `ITradeOutcomeLabeler` interface
  - [ ] `TradeOutcomeLabeler` implementation (with TODOs)
  - [ ] `TradeOutcome` model
  - [ ] Unit tests (HitTarget, HitStop, NoHit, R-multiple, P&L)
  - **Target**: 1 day

- [ ] **1.2** Create `Services/OutcomeSummaryStore.cs`
  - [ ] `OutcomeSummary` model
  - [ ] `IOutcomeSummaryStore` interface
  - [ ] `FileBasedOutcomeSummaryStore` implementation
  - [ ] Unit tests (read/append)
  - **Target**: 0.5 days

- [ ] **1.3** Wire Services into DI & Rollup Reporter
  - [ ] Register in `Program.cs` (lines ~210)
  - [ ] Update `DailyRollupReporter.RunAsync()` signature
  - [ ] Implement `LabelOutcomesAsync()` method
  - [ ] Integration test
  - **Target**: 0.5 days

- [ ] **1.4** Validate on Historical Data
  - [ ] Load 1 week of shadow journal
  - [ ] Run outcome labeler on accepted entries
  - [ ] Spot-check 5–10 trades manually
  - [ ] Document accuracy
  - **Target**: 0.5 days

**Phase 1 Exit Criteria**: ✅ Every accepted signal has a labeled outcome (100%)

---

### PHASE 2: Daily Metrics & Reporting (3–5 days) [DEPENDS ON PHASE 1]
**Status**: ⚠️ 50% Complete | **Blocker**: Phase 1 complete

- [ ] **2.1** Extend `RollupStats` for Outcome Tracking
  - [ ] Add outcome tracking fields (_outcomeWins, _outcomeLosses, etc.)
  - [ ] Implement `RecordOutcome()` method
  - [ ] Create `PerformanceMetrics` class
  - [ ] Implement `GetPerformanceMetrics()` calculator
  - [ ] Unit tests (0%, 50%, 100% win rates; edge cases)
  - **Target**: 1 day

- [ ] **2.2** Enhance Daily Rollup Report
  - [ ] Add "PERFORMANCE METRICS" section to `Render()`
  - [ ] Display: Win Rate, Avg +R, Avg −R, Daily P&L, Expectancy
  - [ ] Add warnings for targets not met
  - [ ] Unit test report structure
  - [ ] Manual test on sample data
  - **Target**: 1 day

- [ ] **2.3** Implement Journal Rotation Service
  - [ ] Create `Services/JournalRotationService.cs`
  - [ ] `IJournalRotationService` interface
  - [ ] `JournalRotationService` implementation
  - [ ] Rotation: `shadow-trade-journal.jsonl` → `shadow-trade-journal-{date}.jsonl`
  - [ ] Integration test (write → rotate → verify)
  - [ ] Register in `Program.cs`
  - **Target**: 1 day

- [ ] **2.4** Integrate Outcomes into Rollup
  - [ ] Load outcomes from `IOutcomeSummaryStore`
  - [ ] Call `stats.RecordOutcome()` for each
  - [ ] Generate `PerformanceMetrics`
  - [ ] Pass to `Render()`
  - [ ] Integration test (full flow)
  - **Target**: 1 day

- [ ] **2.5** Manual Verification & Documentation
  - [ ] Run rollup on 1 week of historical data
  - [ ] Spot-check metrics (win rate, P&L, expectancy)
  - [ ] Document output format
  - [ ] Create test fixtures
  - **Target**: 1 day

**Phase 2 Exit Criteria**: ✅ Daily report includes win rate ≥60%, P&L, expectancy (all metrics present)

---

### PHASE 3: Signal Quality & Data Integrity (2–3 days) [PARALLEL with Phase 2]
**Status**: ✅ 80% Complete | **Blocker**: None

- [ ] **3.1** Implement Post-Signal Quality Checks
  - [ ] Add `MonitorPostSignalQualityAsync()` to `ShadowTradingCoordinator.cs`
  - [ ] Tape slowdown detection (50% threshold)
  - [ ] Spread blowout detection (50% threshold)
  - [ ] Mark canceled signals in journal
  - [ ] Unit tests (detection logic)
  - **Target**: 1 day

- [ ] **3.2** Implement Tape Warm-up Watchlist
  - [ ] Add watchlist tracking to `ShadowTradingCoordinator.cs`
  - [ ] `TapeNotWarmedUp` rejection → add to watchlist
  - [ ] Re-check logic (5 sec min interval)
  - [ ] Unit tests (watchlist add, re-check, spam prevention)
  - [ ] Integration test (symbol rejection → acceptance after tape print)
  - **Target**: 1 day

- [ ] **3.3** Improve Partial Book Handling
  - [ ] Add retry logic to `MarketDataSubscriptionManager.cs`
  - [ ] `PartialBook` flag → retry (max 2 attempts)
  - [ ] Log warnings
  - [ ] Unit tests (retry behavior)
  - [ ] Integration test (partial → retry → success)
  - **Target**: 0.5 days

- [ ] **3.4** Data Quality Validator
  - [ ] Create `Services/DataQualityValidator.cs`
  - [ ] Handle flags: PartialBook, StaleTick, DepthStale
  - [ ] Unit tests (flag interpretation)
  - **Target**: 0.5 days

**Phase 3 Exit Criteria**: ✅ Post-signal cancellations logged correctly (no test failures)

---

### PHASE 4: IBKR Resilience (3–4 days) [PARALLEL with Phase 3]
**Status**: ✅ 70% Complete | **Blocker**: None

- [ ] **4.1** Heartbeat & Disconnect Detection
  - [ ] Add `GetLastTickAgeSeconds()` to `IBkrMarketDataClient.cs`
  - [ ] Add disconnect check to `ShadowJournalHeartbeatService.cs`
  - [ ] Check every 10s, threshold 30s
  - [ ] Market hours detection
  - [ ] `TriggerReconnectAsync()` method
  - [ ] Unit tests (health check, market hours, stale detection)
  - [ ] Integration test (simulate disconnect, verify reconnect triggered)
  - **Target**: 1 day

- [ ] **4.2** Reconnect with Exponential Backoff
  - [ ] Add `DisconnectAsync()`, `ConnectAsync()` to `IBkrMarketDataClient.cs`
  - [ ] Exponential backoff: 2s, 4s, 8s, 16s, 32s (max 60s)
  - [ ] Max 5 retry attempts
  - [ ] `ReSubscribeActiveSymbolsAsync()` after reconnect
  - [ ] Log all attempts and results
  - [ ] Unit tests (backoff delays, attempt cap, successful reset)
  - [ ] Integration test (disconnect → reconnect → re-subscription)
  - **Target**: 1.5 days

- [ ] **4.3** Universe Caching & Fallback
  - [ ] Add cache save/load to `Universe/IbkrScannerUniverseSource.cs`
  - [ ] Cache file: `logs/universe-cache.jsonl`
  - [ ] On scanner failure → load cache (mark as stale)
  - [ ] On scanner success → update cache
  - [ ] Log all operations
  - [ ] Unit tests (cache read/write, fallback on error)
  - [ ] Integration test (success → cache, failure → fallback → recovery)
  - **Target**: 1 day

- [ ] **4.4** Logging & Monitoring
  - [ ] Log reconnect attempts and results
  - [ ] Log cache fallback events
  - [ ] Log scanner failures/successes
  - [ ] Log depth subscription errors (10092 handling)
  - [ ] Sample logs reviewed
  - **Target**: 0.5 days

**Phase 4 Exit Criteria**: ✅ System recovers from IBKR disconnect within 30s (≤30s downtime)

---

### PHASE 5: MVP Scope Discipline (Continuous)
**Status**: ✅ 100% Complete | **Blocker**: None

- [ ] **5.1** Enforce Scope Boundaries
  - [ ] IBKR only (no Alpaca/Polygon)
  - [ ] No new UI (logs + daily rollup = "UI")
  - [ ] No alerting (shadow mode only)
  - [ ] Execution disabled by default
  - [ ] Decision gate: Does it increase edge? Resilience? Profitability metrics?

**Phase 5 Exit Criteria**: ✅ Zero scope creep (all PRs justified by ROI)

---

## Implementation Timeline

```
Week 1:
  Mon 1/20: Phase 1.1–1.2 (TradeOutcomeLabeler, OutcomeSummaryStore)
  Tue 1/21: Phase 1.3–1.4 (DI wiring, validation)
  Wed 1/22: Phase 2.1–2.2 (RollupStats, report output)
  Thu 1/23: Phase 2.3–2.4 (journal rotation, outcome loading)
  Fri 1/24: Phase 2.5 (manual verification) + Phase 3.1 (post-signal filters) START

Week 2:
  Mon 1/27: Phase 3.2–3.4 (watchlist, partial book, validator) + Phase 4.1 (heartbeat) START
  Tue 1/28: Phase 4.2 (reconnect logic)
  Wed 1/29: Phase 4.3–4.4 (caching, logging)
  Thu 1/30: Integration testing, bug fixes
  Fri 1/31: Final validation, documentation

Target Completion: Jan 31, 2026 (15 days from start)
```

---

## Daily Standup Template

**Date**: [DATE]  
**Phase**: [PHASE NUMBER]  
**Task**: [TASK ID] – [TASK NAME]

**Completed Yesterday**:
- [ ] What was done?
- [ ] Any blockers encountered?

**Plan for Today**:
- [ ] What are you starting?
- [ ] Expected completion time?
- [ ] Any dependencies or blockers foreseen?

**Metrics**:
- [ ] Code coverage % (target: ≥80%)
- [ ] Tests passing: X/X
- [ ] Lines of code added

---

## Success Criteria Summary

| Phase | Criterion | Target | Status |
|-------|-----------|--------|--------|
| **1** | Every accepted signal has labeled outcome | 100% | 🔴 Not Started |
| **2** | Daily report: win rate, P&L, expectancy | All present | 🔴 Not Started |
| **3** | Post-signal cancellations logged correctly | No test failures | 🔴 Not Started |
| **4** | IBKR disconnect recovery time | ≤30 seconds | 🔴 Not Started |
| **5** | Scope creep control | Zero low-ROI features | 🟢 Maintained |

---

## Risk Mitigation Checklist

- [ ] IBKR historical data unavailable → fallback to tick-by-tick replay or day's high/low
- [ ] Outcome labeling inaccuracy → validate with 10 manual spot-checks
- [ ] Reconnect loop failure → test with manual TWS stop-start
- [ ] Cache corruption → implement JSON validation + backup strategy
- [ ] Signal quality filters too aggressive → start loose, tighten based on data

---

## File Changes Summary

**New Files**:
- [ ] `Services/TradeOutcomeLabeler.cs` (interface + implementation)
- [ ] `Services/OutcomeSummaryStore.cs` (interface + FileBasedOutcomeSummaryStore)
- [ ] `Services/JournalRotationService.cs` (interface + implementation)
- [ ] `Services/DataQualityValidator.cs` (implementation)
- [ ] `RamStockAlerts.Tests/OutcomeLabelingTests.cs` (unit tests)
- [ ] `RamStockAlerts.Tests/DailyRollupTests.cs` (unit tests)
- [ ] `RamStockAlerts.Tests/PostSignalQualityTests.cs` (unit tests)
- [ ] `RamStockAlerts.Tests/IBkrReconnectTests.cs` (unit tests)

**Modified Files**:
- [ ] `Program.cs` (DI registrations)
- [ ] `Services/DailyRollupReporter.cs` (outcome loading + metrics)
- [ ] `Services/ShadowTradingCoordinator.cs` (post-signal checks, watchlist)
- [ ] `Services/MarketDataSubscriptionManager.cs` (partial book retry)
- [ ] `Services/ShadowJournalHeartbeatService.cs` (heartbeat check)
- [ ] `Feeds/IBkrMarketDataClient.cs` (disconnect detection, reconnect)
- [ ] `Universe/IbkrScannerUniverseSource.cs` (cache fallback)

**Models**:
- [ ] `Models/OutcomeSummary.cs` (new model)
- [ ] `Models/TradeOutcome.cs` (new model)
- [ ] `Models/PerformanceMetrics.cs` (new model)

---

## Next Steps

1. **Start Phase 1.1** – Create `TradeOutcomeLabeler.cs` service
2. **Set up IDE** – Ensure build passes, tests run
3. **Daily standup** – Review this checklist daily
4. **Report progress** – Update status column as tasks complete
5. **Escalate blockers** – Log issues immediately

---

## Notes & Refinements

**Add refinements below:**

---

**Questions?** Refer to `IMPLEMENTATION_ROADMAP.md` for detailed task descriptions, code skeletons, and acceptance criteria.
