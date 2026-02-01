# Trading System Glossary

Definitions of domain-specific trading terms, system concepts, and acronyms used in RamStockAlerts.

---

## Trading Terms

### Absorption
A large bid wall that "absorbs" selling pressure without the price moving lower. Indicates strong buying interest at a specific price level. Can be genuine accumulation or manipulation (see **Flash Wall**).

**Example:** A 50,000 share bid at $10.00 that absorbs multiple sell orders without disappearing.

---

### Ask / Offer
The lowest price at which a seller is willing to sell shares. The "ask side" of the order book represents selling pressure.

**Example:** Ask = $10.01 means the cheapest shares available for purchase cost $10.01.

---

### Bid
The highest price at which a buyer is willing to purchase shares. The "bid side" of the order book represents buying pressure.

**Example:** Bid = $10.00 means the highest price someone is willing to pay is $10.00.

---

### Bid-Ask Ratio
Ratio of cumulative bid size to cumulative ask size across multiple price levels (typically top 5-10 levels).

**Calculation:** `Total Bid Size / Total Ask Size`

**Interpretation:**
- Ratio > 2.0: Strong buying pressure
- Ratio < 0.5: Strong selling pressure
- Ratio ≈ 1.0: Balanced order book

**Example:** 100,000 shares on bid across 5 levels, 40,000 shares on ask across 5 levels → Ratio = 2.5 (bullish).

---

### Bracket Order
A set of three orders submitted together:
1. **Entry Order:** Market or limit order to establish position
2. **Profit Target:** Limit order to exit at profit
3. **Stop Loss:** Stop order to exit if trade goes against you

**Purpose:** Risk-defined trading. Automatically manages both upside and downside.

**Example:**
- Entry: Buy 500 shares at $10.00 (market order)
- Profit Target: Sell 500 shares at $10.50 (limit order)
- Stop Loss: Sell 500 shares at $9.90 (stop order)

**Max Risk:** $50 (500 shares × $0.10)
**Max Profit:** $250 (500 shares × $0.50)

---

### Float
The number of shares available for public trading (excludes insider holdings, restricted shares, etc.).

**Low Float:** < 10M shares (typically more volatile)
**Medium Float:** 10M - 50M shares (RamStockAlerts target range: 5M - 50M)
**High Float:** > 100M shares (typically less volatile)

**Why It Matters:** Low float stocks can be more easily manipulated but also offer larger price moves with less volume.

---

### Flash Wall
A fake large order that appears on Level 2 for a brief period (2-10 seconds) and then disappears. Used to create false impression of buying/selling pressure.

**Manipulation Tactic:** Designed to trick traders into thinking there's strong support/resistance at a price level.

**Detection:** RamStockAlerts tracks order book changes and rejects signals when large walls appear and disappear rapidly.

**Example:** A 100,000 share bid at $10.00 appears for 3 seconds, then vanishes without being filled.

---

### Iceberg Order
A large order that only shows a small portion on the visible order book. As the visible portion is filled, more shares are revealed.

**Purpose:** Hide true order size to avoid moving the market.

**Detection:** Continuous fills at the same price level without the visible size depleting.

**Example:** A 50,000 share sell order displays as 500 shares on Level 2, but keeps refilling as it gets bought.

---

### Level 1 Data
Basic market data showing:
- Last trade price
- Bid price
- Ask price
- Volume

**Limitation:** Does not show order book depth.

---

### Level 2 Data (Market Depth)
Advanced market data showing:
- All visible bids and asks at multiple price levels
- Size at each price level
- Market maker IDs (on some exchanges)

**Example:**
```
ASK SIDE:
$10.03 → 5,000 shares
$10.02 → 8,000 shares
$10.01 → 12,000 shares

BID SIDE:
$10.00 → 15,000 shares
$9.99  → 10,000 shares
$9.98  → 6,000 shares
```

**Use in RamStockAlerts:** Core data source for order flow analysis and signal scoring.

---

### Liquidity Dislocation
A temporary imbalance between supply and demand that creates a trading edge. Occurs when order book becomes one-sided (heavy bid or heavy ask).

**Characteristics:**
- Rapid bid-ask ratio change
- Spread compression
- Increasing tape velocity
- Strong Level 2 depth on one side

**RamStockAlerts Strategy:** Detect these dislocations early and enter before the larger market reacts.

---

### Market Order
Order to buy or sell immediately at the best available price. Guarantees execution but not price.

**Risk:** Slippage in fast-moving or illiquid markets.

**Use in RamStockAlerts:** Entry orders for high-probability setups where speed matters more than exact price.

---

### Limit Order
Order to buy or sell at a specific price or better. Guarantees price but not execution.

**Use in RamStockAlerts:** Profit targets (sell at target price) and defensive entries (buy only if price reaches specific level).

---

### Spread
The difference between the best bid and best ask.

**Calculation:** `Ask Price - Bid Price`

**Example:**
- Bid: $10.00
- Ask: $10.02
- Spread: $0.02

**RamStockAlerts Filter:** Spread must be $0.02 - $0.05 (tight enough for scalping but wide enough to indicate genuine price discovery).

**Why It Matters:**
- Tight spread ($0.01): Very liquid, but less opportunity
- Wide spread ($0.10+): Illiquid, high slippage risk
- Optimal spread ($0.02-$0.05): Liquid enough to trade, wide enough for profit

---

### Spread Compression
The spread tightening over time, indicating increased liquidity and price agreement between buyers and sellers. Often precedes a directional move.

**Example:**
- 10:00 AM: Spread = $0.05
- 10:05 AM: Spread = $0.03
- 10:10 AM: Spread = $0.02 (compression)

**Bullish Signal:** Spread compressing while bid pressure increases.

---

### Stop Loss Order
Order that becomes a market order when a specific price is reached. Used to limit losses.

**Example:** Buy 500 shares at $10.00, set stop loss at $9.90. If price drops to $9.90, sell immediately.

**Use in RamStockAlerts:** Every bracket order includes a stop loss to cap risk at 0.25% of account.

---

### Tape / Time & Sales
Real-time stream of executed trades showing:
- Time of trade
- Price
- Size
- Exchange

**Use in RamStockAlerts:** Analyze trade velocity and buying/selling aggression.

---

### Tape Velocity
The speed at which trades are executing (trades per second or trades per minute).

**Interpretation:**
- High velocity with rising price: Strong buying pressure
- High velocity with falling price: Strong selling pressure
- Low velocity: Consolidation, indecision

**RamStockAlerts Scoring:** Higher velocity during bid pressure increases = higher signal score.

---

### VWAP (Volume-Weighted Average Price)
The average price weighted by volume. Institutional traders often use VWAP as a benchmark.

**Calculation:** `Sum(Price × Volume) / Total Volume`

**Interpretation:**
- Price above VWAP: Bullish (buyers willing to pay above average)
- Price below VWAP: Bearish (sellers accepting below average)

**VWAP Reclaim:** Price crossing back above VWAP with momentum and bid pressure. Strong bullish signal.

---

### VWAP Reclaim
A price pattern where:
1. Price drops below VWAP
2. Strong bid pressure builds
3. Price crosses back above VWAP
4. Tape shows aggressive buying

**Historical Win Rate (per ProductGoalV2.md):** 72% when combined with bid-ask ratio > 2.0.

**Use in RamStockAlerts:** Adds 1.5 points to signal score when detected.

---

## System Concepts

### Signal Score
A 0-10 numerical rating of a trading opportunity. Combines multiple order flow metrics into a single score.

**Components:**
1. Bid-Ask Ratio (weight: high)
2. Spread Compression (weight: medium)
3. Tape Velocity (weight: high)
4. Level 2 Depth (weight: high)
5. VWAP Reclaim (weight: medium)
6. Other proprietary metrics

**Threshold:** 8.0 minimum to generate an alert and execute a trade.

**Example:** A signal with strong bid pressure (3.0 ratio), tight spread ($0.02), and VWAP reclaim might score 8.7/10.

---

### Gate
A validation check that a signal must pass before execution. Gates can:
- **Hard Reject:** Instantly reject the signal (e.g., spread too wide)
- **Soft Reject:** Reduce the signal score (e.g., lower volume than ideal)
- **Pass:** Allow signal to proceed unchanged

**Purpose:** Filter out false signals and manipulation.

---

### Hard Gate
A validation check that results in instant rejection if failed. No score reduction—signal is completely rejected.

**Examples:**
- Spread > $0.05 → Hard Reject
- Price outside $2-$20 range → Hard Reject
- Float outside 5M-50M shares → Hard Reject

---

### Soft Gate
A validation check that reduces the signal score if conditions are suboptimal, but doesn't fully reject the signal.

**Example:** Volume slightly below ideal might reduce score by 0.5 points, but signal can still pass if other metrics are strong.

---

### Gate Trace
An audit trail showing which gates a signal passed through and their results. Used for debugging and strategy refinement.

**Example:**
```
Signal for AAPL at 10:00:00
├─ HardGate: PASS (spread = $0.03)
├─ AbsorptionReject: PASS (no flash walls detected)
├─ FloatGate: PASS (float = 15.5M)
├─ VolumeGate: SOFT_REDUCE (-0.3 points, volume below ideal)
└─ Final Score: 8.4/10 → ACCEPTED
```

---

### Position Sizing
Calculation of how many shares to trade based on:
- Account size
- Risk per trade (0.25% of account)
- Stop loss distance

**Formula:**
```
Shares = (Account Size × Risk %) / (Entry Price - Stop Price)
```

**Example:**
- Account Size: $40,000
- Risk Per Trade: 0.25% = $100
- Entry: $10.00
- Stop: $9.80
- Distance: $0.20

**Shares:** $100 / $0.20 = 500 shares

**Hard Limit:** 400-600 shares per trade (prevents moving Level 2 ask).

---

### Risk Per Trade
The maximum amount of capital at risk on a single trade.

**RamStockAlerts Setting:** 0.25% of account size ($75-$100 on a $40K account).

**Purpose:** Ensures that even a string of losses won't significantly damage the account.

---

### Profit Factor
Key performance metric calculated as:

**Formula:** `Gross Wins / Gross Losses`

**Interpretation:**
- Profit Factor = 1.0: Break even
- Profit Factor > 2.0: Target (every $1 lost generates $2+ in wins)
- Profit Factor < 1.5: Abort signal (strategy not profitable)

**Example:**
- Gross Wins: $5,000
- Gross Losses: $2,000
- Profit Factor: 2.5

---

### Win Rate
Percentage of trades that are profitable.

**Formula:** `(Winning Trades / Total Trades) × 100`

**RamStockAlerts Target:** ≥ 60%

**Example:** 60 wins out of 100 trades = 60% win rate.

---

### Drawdown
Peak-to-trough decline in account value.

**Daily Drawdown:** Max loss allowed in a single day ($600 for RamStockAlerts)
**Monthly Drawdown:** Max cumulative loss in a month (< $3,000)

**Purpose:** Protect capital during losing streaks. System auto-halts at daily drawdown limit.

---

### Shadow Mode
Operating mode where signals are scored and logged, but NO trades are executed. Used to validate strategy without risking capital.

**Use Case:** Test new signal logic or validate performance in live market conditions before going live.

---

### Live Mode
Operating mode where signals are executed as real trades with real money.

**Risk Controls:** All gates, position sizing, and drawdown limits are active.

---

### Replay Mode
Operating mode where historical data for a specific symbol is replayed to backtest the strategy.

**Usage:**
```bash
export MODE=replay
export SYMBOL=AAPL
dotnet run --project ./src/RamStockAlerts/RamStockAlerts.csproj
```

**Purpose:** Test signal logic against historical price action to refine parameters.

---

## Acronyms

### IBKR
Interactive Brokers - the brokerage providing market data and order execution.

---

### TWS
Trader Workstation - Interactive Brokers' desktop trading platform. RamStockAlerts connects to TWS API for market data and order submission.

**Default Port:** 7497 (live trading), 7496 (paper trading)

---

### P&L
Profit and Loss - the net result of closed trades.

**Gross P&L:** Profit before commissions
**Net P&L:** Profit after commissions

---

### API
Application Programming Interface - programmatic access to external systems.

**Examples in RamStockAlerts:**
- IBKR API: Market data and order execution
- Polygon API: Supplemental market data
- Alpaca API: Paper trading and additional market data

---

### EF Core
Entity Framework Core - Microsoft's object-relational mapper (ORM) for .NET. Used for database access in RamStockAlerts.

---

### ORM
Object-Relational Mapping - technique for converting data between incompatible type systems (database tables ↔ C# objects).

---

### REST
Representational State Transfer - architectural style for building web APIs. RamStockAlerts exposes a REST API for control and monitoring.

---

### DTO
Data Transfer Object - simple object used to transfer data between layers or systems. Used extensively in the Execution module.

---

### JSON
JavaScript Object Notation - text format for data exchange. Used in configuration files (appsettings.json) and API responses.

---

## Risk Management Terms

### Account Ceiling
Maximum account size before strategy edge degrades.

**RamStockAlerts Limit:** $100K

**Reason:** At larger account sizes, 400-600 share positions become too small (risk < 0.25%), but larger positions would move the Level 2 ask and degrade execution quality.

---

### Slippage
The difference between expected execution price and actual execution price.

**Example:**
- Expected Entry: $10.00
- Actual Fill: $10.02
- Slippage: $0.02 (or $10 on a 500 share order)

**Mitigation:** Trade only symbols with tight spreads ($0.02-$0.05) and moderate volume.

---

### Commission
Fee charged by the broker per trade.

**Typical IBKR Commission:** $0.005 per share, $1.00 minimum

**Example:** 500 shares = $2.50 commission

**Impact:** On a $100 gross profit, $2.50 commission = $97.50 net profit.

---

## Time-Based Terms

### Pre-Market
Trading session before regular market hours (04:00 - 09:30 ET).

**RamStockAlerts Activity:** Universe building at 09:25 ET.

---

### Regular Hours
Main trading session: 09:30 - 16:00 ET (Eastern Time).

**RamStockAlerts Active Windows:**
- 09:30 - 10:30: Primary (3 max trades)
- 10:30 - 11:30: Secondary (2 max trades)
- 14:00 - 15:00: Final window (2 max trades, conditional)

---

### Dark Period
Time when RamStockAlerts does NOT trade due to low probability conditions.

**Dark Period:** 11:30 - 14:00 ET (lunchtime lull, choppy price action, low volume).

---

## Order Book Structure Terms

### Top of Book
The best bid and ask prices (first level of Level 2 data).

**Example:**
- Best Bid: $10.00 (15,000 shares)
- Best Ask: $10.01 (12,000 shares)

---

### Depth of Book
The cumulative size and number of price levels beyond the top of book.

**Example:**
```
5 levels deep on bid side: 50,000 total shares
5 levels deep on ask side: 30,000 total shares
```

**Interpretation:** Heavy bid depth suggests strong support.

---

### Bid Wall
A large number of buy orders at a specific price level.

**Example:** 100,000 shares bid at $10.00.

**Genuine vs Fake:**
- Genuine: Stays in place and absorbs selling
- Fake (Flash Wall): Disappears when tested

---

### Ask Wall
A large number of sell orders at a specific price level.

**Example:** 100,000 shares offered at $10.50.

**Purpose:** Acts as resistance. Can be genuine supply or manipulation to prevent upward price movement.

---

## Related Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - System design using these concepts
- [API.md](API.md) - REST endpoints referencing these terms
- [EXAMPLES.md](EXAMPLES.md) - Real-world examples showing these concepts in action
- [ProductGoalV2.md](../ProductGoalV2.md) - Strategic goals and performance targets
