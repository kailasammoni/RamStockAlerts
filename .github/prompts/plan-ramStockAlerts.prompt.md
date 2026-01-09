RamStockAlerts – IBKR Order-Flow Intelligence Platform
ROLE

You are building a professional microstructure trading system using Interactive Brokers Level-II data.

You are detecting liquidity failure points — not momentum.

OBJECTIVE

Detect real order-book imbalance events using true L2 depth and tick-by-tick tape.

PHASE 1 — MARKET DATA CLIENT

Create:

Feeds/IBkrMarketDataClient.cs


Using official IBApi C# SDK.

Subscribe to:

client.reqMktDepth(tickerId, contract, 10, false, null);
client.reqTickByTickData(tickerId, contract, "AllLast", 0, false);

PHASE 2 — ORDER BOOK STATE
Models/OrderBookState.cs


Maintain:

BidDepth[price] = size

AskDepth[price] = size

LevelAge[price] = timestamp

PHASE 3 — LIQUIDITY METRICS
Engine/OrderFlowMetrics.cs


Compute:

Metric	Definition
QueueImbalance	ΣBidDepth[0..3] / ΣAskDepth[0..3]
WallPersistence	LevelAge ≥ 1000ms
AbsorptionRate	Δ(Size)/Δ(Time)
SpoofScore	CancelRate / AddRate
TapeAcceleration	Δ(trades/sec) over 3s
PHASE 4 — SIGNAL ENGINE
Engine/OrderFlowSignalValidator.cs


Trigger when:

QueueImbalance ≥ 2.8
WallPersistence ≥ 1000ms
AbsorptionRate > threshold
SpoofScore < 0.3
TapeAcceleration ≥ 2.0

PHASE 5 — SAFETY CONTROLS

Engine pause on IB disconnect

Symbol cooldown: 10 minutes

Global cap: 3 alerts/hour

FINAL DIRECTIVE

Never infer liquidity.

Only react to measured order-book behavior.