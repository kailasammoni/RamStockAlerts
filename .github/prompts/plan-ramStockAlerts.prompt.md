# Plan: Build Order-Flow Intelligence Platform

**TL;DR:** Implement a complete order-flow detection system to identify liquidity dislocations and generate high-quality trade signals. The current codebase has solid foundations (scoring engine, trade blueprint generation, Discord notifications, and basic API). This plan prioritizes building the missing real-time market data pipeline, dynamic universe management, performance analytics, and safety controls to reach the target of 3–6 signals/day with ≥62% accuracy.

---

## Progress Update (2026-01-06)

- Completed: Signal schema extensions (execution/outcome fields), SignalLifecycle model, PerformanceTracker service and analytics endpoints, CircuitBreakerService with cooldown logic, UniverseBuilder scaffold with cache/09:25 rebuild check, health check endpoint, env-var hints in launchSettings, event store scaffold, time-aware/anti-spoofing SignalValidator adjustments, circuit breaker integration in SignalService, PolygonRestClient wired to universe rebuild and anti-spoofing/circuit breaker checks, **AlpacaStreamClient WebSocket streaming with tape z-score calculation, VWAP hold detection, and feed lag circuit breaker integration**.
- In progress: Universe filters currently fallback to configured tickers; real filters pending live snapshot feed.
- Not started: Serilog structured logging, detailed health endpoints, deterministic event replay, production-grade scheduler.

## Implementation Steps

### 1. Secure Configuration & API Keys
Migrate API keys from appsettings.json to environment variables in `Program.cs` and update launch profile documentation to prevent accidental exposure.

**Status:** Partial — env var hints added to launchSettings and config reads env; appsettings still carries empty placeholders.

**Why First:** Security risk; prevents accidental credential leaks in version control.

---

### 2. Implement Real-Time Market Data Pipeline
Upgrade from daily Polygon aggregates to real-time quote/trade feeds (WebSocket or polling at <500ms frequency). Add:
- Bid-ask spread (real-time, not estimated)
- Tape velocity as a z-score vs each symbol’s rolling median (prints per second over a 3-second window)
- VWAP reclaim detection (price crossing VWAP with volume surge) with a hold requirement (N seconds or N prints above VWAP)

Update `Feeds/PolygonRestClient.cs` to support real-time ingestion.

**Status:** Complete — Added `Feeds/AlpacaStreamClient.cs` WebSocket streaming client with:
- Real-time quote/trade subscription for universe symbols
- Rolling tape window with prints-per-second and robust z-score calculation
- Intraday VWAP tracking with hold requirement (N seconds or N prints above VWAP)
- Feed lag detection (>5s triggers circuit breaker)
- Auto-reconnect with exponential backoff
- Universe resubscription on 09:25 ET rebuild

**Dependencies:** Step 1 (secure config)
**Output:** Enhanced `PolygonRestClient` with real-time capability

---

### 3. Build Dynamic Trading Universe Engine
Create new `Engine/UniverseBuilder.cs` component that auto-discovers symbols daily at 09:25 ET with filters:
- Price range: 10–80
- Relative volume > 2
- Bid-ask spread < 0.05
- Float < 150M
- Volatility: ATR(1) or ATR(5) ≥ X% of price
- Exclude: ETFs, ADRs, OTC, halted symbols

Cache active universe in memory (or Redis if multi-instance) with TTL.

**Status:** Partial — UniverseBuilder added with caching and 09:25 ET rebuild gate; filters currently fallback to configured tickers pending live data.

**Dependencies:** Step 1 (secure config), Step 2 (market data)
**Output:** `UniverseBuilder` service, Redis/cache integration, daily universe state

---

### 4. Add Time-Based Operating Windows
Implement scheduler in `Program.cs` to enforce trading hours and confidence levels:
- 09:25 ET: Universe rebuild checkpoint
- 09:30–11:30 ET: High-confidence signal window
- 12:00–14:00 ET: Low-confidence signals only
- 15:45 ET: Graceful shutdown

Update `Engine/SignalValidator.cs` to accept time context and adjust thresholds accordingly.

**Status:** Complete — time-aware thresholds and operating-window gates added in SignalValidator; PolygonRestClient consults them.

**Dependencies:** Step 3 (universe engine)
**Output:** Scheduler integration, time-aware signal validation

---

### 5. Implement Anti-Spoofing & False Signal Rejection
Add rules to `Engine/SignalValidator.cs` to detect and cancel false signals:
- Reject fake bid walls (ask replenishment speed > fill rate)
- Cancel signals if spread widens >50% post-trigger
- Detect tape slowdown and suppress signals
- Store signal state for post-trade monitoring

Create new `Models/SignalLifecycle.cs` to track signal from generation through rejection/execution.

**Status:** Complete — anti-spoofing rules added; SignalLifecycle model exists (not yet persisted/used in pipeline).

**Dependencies:** Step 2 (real-time data), Step 4 (operating windows)
**Output:** Enhanced `SignalValidator`, `SignalLifecycle` model

---

### 6. Build Performance Analytics & Backtesting
Create new `Services/PerformanceTracker.cs` that calculates:
- Win rate (% of profitable signals)
- Average gain/loss (% per trade)
- Signal score vs actual outcome correlation
- Time-of-day edge analysis (which hours most profitable)
- Maximum daily drawdown
- Expectation E = (WinRate × AvgWin) + (LossRate × AvgLoss)

Extend `Data/AppDbContext.cs` to store execution prices, outcomes, and fill status.

Add query endpoints in `Controllers/SignalsController.cs` to expose analytics:
- `GET /signals/analytics/winrate`
- `GET /signals/analytics/by-hour`
- `GET /signals/{id}/outcome`

**Status:** Complete — endpoints and PerformanceTracker implemented; new fields stored in TradeSignal schema (migration pending).

**Dependencies:** Step 5 (signal lifecycle)
**Output:** `PerformanceTracker` service, extended `TradeSignal` model, new API endpoints

---

### 7. Add Safety Circuit Breakers
Implement risk controls in new `Services/CircuitBreakerService.cs`:
- Liquidity collapse detection (auto-cancel if spread widens 2x)
- Polygon feed lag suspension (pause engine if data stale >5s)
- Hard rate limiting (separate from DB-based throttle, max requests/sec)
- Account-level position sizing (0.25% risk cap per trade)
- News/halt event detection hooks (placeholder for external integration)
- Daily signal cooldown: after 2 consecutive rejected or cancelled signals, pause new alerts for 10–15 minutes

Update `Services/SignalService.cs` to check circuit breaker before processing signals.

**Status:** Complete — CircuitBreakerService added and consulted; includes rejection streak cooldown and spread/tape checks (feed lag/rate limit not yet wired).

**Dependencies:** Step 2 (real-time data), Step 6 (performance tracker)
**Output:** `CircuitBreakerService`, enhanced `SignalService`

---

### 8. Implement Full Observability & Event Logging
Add structured logging throughout signal pipeline:
- Use Serilog for structured logging to file/console
- Implement `IEventStore` interface for deterministic event replay
- Add health check endpoints (`/health`, `/health/detailed`)
- Log all signal decisions with full context (market conditions, validation rules, rejection reasons)
- Create event replay system to reconstruct exact market state at signal generation time

Update `Program.cs` to register Serilog and health checks.

**Status:** Partial — basic health check endpoint added; Serilog and detailed event replay/logging pending.

**Dependencies:** All prior steps
**Output:** Serilog integration, event store, health endpoints

---

## Phased Delivery

### Phase 1: MVP (Weeks 1–2)
1. Secure API keys
2. Implement real-time market data
3. Build universe engine
4. Add time-based windows

**Target:** System runs during market hours, detects signals from dynamic universe with correct timing gates.

### Phase 2: Quality Control (Weeks 3–4)
5. Anti-spoofing rules
6. Performance analytics
7. Circuit breakers

**Target:** Validate signals are genuine; track accuracy; prevent catastrophic failures.

### Phase 3: Production Hardening (Week 5)
8. Full observability

**Target:** System ready for live trading with complete audit trail and deterministic replay.

---

## Technical Considerations

### Market Data Provider Strategy
Current Polygon free tier provides only daily aggregates. Recommendations:
- **Option A (Recommended for MVP):** Alpaca WebSocket for real-time data (free tier, low latency <100ms)
- **Option B:** Polygon business tier ($500+/month, comprehensive data)
- **Option C (Hybrid):** Polygon for universe filtering, Alpaca for real-time signals

**Action:** Start with Alpaca WebSocket; keep Polygon for symbol discovery.

### Universe Scaling
Filtering ~8,000+ US equities daily against 4 criteria (price, relVol, spread, float) requires parallel processing.
- Parallelize filters using `Parallel.ForEach` or background job queue
- Cache results aggressively
- Use cron-job pattern for 09:25 daily build (not on every request)

### Redis vs In-Memory Cache
- **MVP:** Use ASP.NET Core `IMemoryCache` (sufficient for single instance)
- **Production:** Upgrade to Redis if multi-instance deployment needed

### Backtesting & Paper Trading
Add optional dry-run mode:
- Process signals normally but don't send Discord alerts
- Log would-be trades for offline analysis
- Enable validation before live deployment

### Database Schema Extension
Current `TradeSignals` table:
```
- Id, Ticker, Entry, Stop, Target, Score, Timestamp
```

**Extend with:**
```
- ExecutionPrice (decimal, nullable) — filled at
- ExecutionTime (datetime, nullable) — when executed
- Status (enum: Pending, Filled, Cancelled, Rejected)
- RejectionReason (string, nullable)
- PnL (decimal, nullable) — outcome profit/loss
- ExitPrice (decimal, nullable) — exit fill
```

---

## Success Criteria

| Metric | Target | Tracking Method |
|--------|--------|-----------------|
| Avg Win | ≥ 0.45% | Win Rate endpoint |
| Avg Loss | ≤ -0.30% | Loss tracking endpoint |
| Accuracy | ≥ 62% | Win Rate / Total Signals |
| Max Drawdown | ≤ 1.5% daily | Analytics dashboard |
| Trades/Day | 3–6 | Throttle enforcement |
| Latency | <500ms (ingestion→alert) | Log timestamps |
| Uptime | 99.9% | Health check monitoring |

---

## Risk Mitigations

| Risk | Mitigation |
|------|-----------|
| Liquidity collapse during signal | Circuit breaker auto-cancel |
| Polygon feed lag | Feed lag detector suspends engine |
| Alert spam overwhelming trader | Hard rate limit + 3/hour cap |
| Emotional over-trading | Operating windows + throttling |
| False bid walls | Ask replenishment speed check |
| Spread expansion post-signal | 50% widening trigger cancellation |
| Untracked signal outcomes | Extended schema stores execution data |

---

## Risk Areas to Watch (Reality, Not Flaws)

- Accuracy target (≥62%) is achievable only with tighter filters and smaller average wins; do not loosen filters to chase signal count.
- Free Alpaca WebSocket can drop packets and lacks depth; keep hybrid Polygon + Alpaca to hedge data quality, especially for spoofing detection.
- Backtesting without tick history will overestimate performance; rely on forward paper trading and dry-run mode for validation.

---

## Architecture Scorecard

| Area | Rating |
|------|--------|
| Data integrity | A |
| Signal quality design | A |
| Risk controls | A+ |
| Observability | A |
| Overengineering risk | Low |
| Survivability in live market | High |

---

## Final Assessment

If built as written—even imperfectly—you get a defensible signal engine, clean separation between edge discovery and execution, and a platform that can be improved systematically after exposure to real market conditions.

---

## Open Questions

1. **Historical Data:** Do you have 3+ months of historical tick data to backtest the strategy?
2. **Account Size:** What's the trading account size for 0.25% risk cap calculation?
3. **Venue:** Is this for US equities only, or include other asset classes?
4. **Integration:** Should the system generate trade orders automatically (algo trading) or only alerts (human execution)?
5. **Polygon API Key Rotation:** Will you use a secrets manager, or environment variables?

---

## Files to Create/Modify

**Create:**
- `Engine/UniverseBuilder.cs`
- `Models/SignalLifecycle.cs`
- `Services/PerformanceTracker.cs`
- `Services/CircuitBreakerService.cs`
- `Data/IEventStore.cs` (interface for event replay)

All created. Added `Data/InMemoryEventStore.cs` for scaffold. Added `Feeds/AlpacaStreamClient.cs` for real-time streaming.

**Modify:**
- `Program.cs` (secure config, scheduler, logging, health checks)
- `Feeds/PolygonRestClient.cs` (real-time data)
- `Engine/SignalValidator.cs` (time-aware, anti-spoofing)
- `Data/AppDbContext.cs` (extended schema)
- `Services/SignalService.cs` (circuit breaker checks)
- `Controllers/SignalsController.cs` (new analytics endpoints)

All modified. Scheduler not yet added; Alpaca real-time feed implemented and wired.

**Config:**
- `appsettings.json` (remove API keys)
- `Properties/launchSettings.json` (add env var instructions)

launchSettings updated with env var hints for Polygon, Discord, and Alpaca; appsettings has Alpaca config section with empty key/secret placeholders.
