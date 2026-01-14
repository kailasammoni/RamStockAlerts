# Runbooks

## Current Status
- Shadow mode is live: IBKR depth and tape feeds drive `ShadowTradingCoordinator`, blueprints are scored, and every accepted or rejected signal pairs the book and tape context before writing to the journal.
- The shadow journal writes SchemaVersion=2 entries, captures depth/tape snapshots and heartbeat markers, and keeps a stable format for replay, reporting, and performance tracking.
- Tape and depth requests are now paired and backed by `DepthEligibilityCache` plus `HandleIbkrError`, so 10092 depth ineligible errors disable depth cleanly before new attempts flood the symbol.
- The recorder-only service (`MODE=record` / `Ibkr:Mode=Record`) is available to capture raw IBKR depth/tape JSONL under `logs/ibkr-*` for diagnostics and offline review.

## Known Issues
- 10092 depth ineligibility continues to surface for symbols without full contract details or when IB enforces depth limits; we log the rejection, mark the symbol ineligible, and skip another depth attempt for several minutes, which can leave the order book without depth while tape remains active.
- Tick-by-tick subscriptions are capped by `MarketData:TickByTickMaxSymbols` (default 10) because IB limits concurrent tick-by-tick feeds; additional symbols wait until slots open or we drop depth so tick-by-tick can restart later.
- Book validity edge cases (crossed books, stale quotes) and scanner relaunch glitches still require moderate manual tuning and attention when the IBKR connection restarts.
- Some journal fields occasionally default while we tune the schema, so replaying rejected signals sometimes needs cleanup before the win-rate pipeline runs.

## How to Run Shadow Mode
1. Keep `TradingMode` set to `Shadow` in `appsettings.json` (default) or override via `set TradingMode=Shadow` / `env:TradingMode=Shadow` before launch; do **not** set `MODE` to `record` or `replay`.
2. Ensure `IBKR:Enabled` is `true`, tape/depth flags are on, and `ShadowTradeJournal:FilePath` points to `logs/shadow-trade-journal.jsonl` (default).
3. Start the app with `dotnet run --project RamStockAlerts.csproj` (or via your preferred host); the console and log files should show `[Shadow]` journal startup plus `[IBKR]` market data logins.
4. Monitor `logs/shadow-trade-journal.jsonl` for SchemaVersion=2 entries and the `Logs/ramstockalerts-*.txt` files for `Shadow`/`IBKR` diagnostics; the journal is the master list feeding next-step analytics.

## How to Run Recorder
1. Set the environment variable `MODE=record` (or `Ibkr:Mode=Record` in configuration) before launching; this short-circuits the normal app and starts `IbkrRecorderHostedService`.
2. Confirm `Ibkr:Host`, `Port`, and `ClientId` match your TWS/Gateway instance, and configure `Ibkr:Symbol`, `DepthRows`, `DepthExchange`, and `RecordMinutes` to your capture plan.
3. Run `dotnet run --project RamStockAlerts.csproj`; the recorder connects, subscribes to depth/tape for the configured symbol, and writes JSONL files like `logs/ibkr-depth-AAPL-YYYYMMDD.jsonl`.
4. The recorder stops after `RecordMinutes` or when the socket disconnects; check the console for `[IBKR Recorder]` logs showing each stage, depth retries, and fallback to tape when needed.

## Next Steps
- Turn SchemaVersion=2 shadow journal entries into outcome-tagged records (TP, SL, miss) so `PerformanceTracker` and rollup reports can compute the realized win rate, avg gain/loss, and accuracy compared to the >=62% / >=0.45% targets.
- Surface those computed metrics alongside trades/day and max drawdown so we can measure progress versus the win-rate goal and feed the data into scarcity gating.
- Use the journal analytics to guide tuning of absorption/tape filters, `DepthEligibilityCache`, and `ScarcityController` rules until the live edge reliably meets the product success criteria.
