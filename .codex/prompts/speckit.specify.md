# Prioritized Action Plan (Money-Making Focus)
TIER 1: PROVE THE EDGE EXISTS 
Without this, everything else is pointless

Build Outcome Tracking Pipeline

Tag each journaled signal as: Win (hit target), Loss (hit stop), or Cancelled
Calculate actual win rate, avg gain/loss, profit factor, max drawdown
Goal: Confirm 62% accuracy and positive expectancy over 100+ sample trades


Run Extended Paper Trading

Enable IBKR paper account connection (change from "Fake" broker)
Run 2-4 weeks of live paper trading during market hours
Collect real fill data, slippage, execution delays
Goal: Prove the system can achieve target metrics (0.45% avg win, 1.5% max drawdown) with realistic execution



TIER 2: FINANCIAL SAFETY CONTROLS 💰
Protect capital from catastrophic loss

Implement Daily Loss Limit Enforcement

Currently a placeholder only ($200 limit configured but not enforced)
Add real-time P&L tracking that auto-triggers kill-switch at loss threshold
Critical: Without this, a losing streak could wipe out the account


Fix Position Sizing Logic

System uses fixed $500 notional instead of 0.25% account risk
Implement dynamic sizing based on actual account equity
Fetch account balance via IBKR API before each trade
Risk: Current approach could over-leverage small accounts or under-utilize large ones


Add Real-Time P&L Monitoring

Track open positions and mark-to-market P&L
Enable intraday adaptive behavior (stop trading after X losses)
Show live profit/loss dashboard for operator oversight



TIER 3: EXECUTION RELIABILITY 🔧
Ensure trades execute as intended

Complete IBKR Integration Testing

Handle partial fills, order rejections, connection drops
Implement fill confirmations and position reconciliation
Test bracket order (OCO) behavior for stop/target
Weakness: Current code doesn't log fills beyond submission - could lose track of positions


Wire Up Auto-Cancellation to Broker

System detects when signals go bad (spread blowout, tape stall) but doesn't cancel live orders
Connect post-signal monitoring to IBKR cancel order API
Add entry order timeout (cancel if unfilled after X seconds)
Risk: Bad trades stay live even after edge vanishes


Add Broker Event Handling

Capture order status updates, fill events, account updates
Prevent system/broker position mismatch
Handle margin calls, short availability checks



TIER 4: OPERATIONAL ROBUSTNESS 🛡
Run unattended without breaking

Build Monitoring & Alerting

Health checks for data feed and broker connectivity
Alert on critical errors (order rejections, feed lag, connectivity loss)
Automated recovery or graceful shutdown on failures


Validate Universe Selection in Live Markets

Stress-test that scanner returns stocks meeting criteria (RelVol >2, Spread <$0.05)
Confirm filters exclude halts, ETFs, illiquid names
Run for multiple days to ensure stability


Test Scalability Under Load

Verify <500ms latency holds during volatile markets
Confirm throttling (3 depth subscriptions, 250ms eval) doesn't miss best opportunities
Measure actual throughput: does it achieve 3-6 quality trades/day target?




Recommended Path to Live Trading
Phase 1 (2-3 weeks): Paper Trading Validation

Turn on IBKR paper account execution
Run daily during 9:30-11:30 AM window
Collect 50-100+ signal outcomes
Gate: Only proceed if win rate 60% and profit factor >1.5

Phase 2 (1 week): Risk Controls Hardening

Implement daily loss limit enforcement
Fix position sizing to true account %
Add P&L tracking and auto-shutdown logic
Complete auto-cancel wiring

Phase 3 (1-2 weeks): Live Micro-Testing

Connect to live IBKR account with minimal capital ($1,000-2,000 max)
Trade smallest allowed share sizes (10-50 shares)
Run for 2 weeks monitoring every execution detail
Gate: Zero unexpected behavior, P&L matches paper trading results

Phase 4: Scale Gradually

Increase position sizes to target 0.25% account risk
Expand capital allocation only after consistent profitability
Continuously monitor win rate vs. 62% target
