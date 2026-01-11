SYSTEM GOAL

Detect transient order-book imbalances using IBKR Level II + tape data that statistically precede short-term price continuation, and deliver high-quality, human-executed trade blueprints.

DATA SOURCE (LOCKED)

Interactive Brokers (IBKR)

Nasdaq TotalView + NYSE OpenBook

Real Level II depth + Time & Sales

No Schwab / no quote-only mode

STRATEGY CLASS

Order-flow / liquidity dislocation

Pre-price structure, not post-momentum

NON-NEGOTIABLE RULES

No signals when order book is invalid

Crossed or locked book = INVALID

Replay determinism required before live shadow trading

Scarcity > frequency (3–6 trades/day max)

SUCCESS CRITERIA

Win rate: 62–68%

Avg win: ≥ +0.45R

Avg loss: ≤ −1R

Max daily drawdown: ≤ 1.5%

Monthly target (@ $25–30k): $1,000+

CURRENT STATUS

IBKR Level II verified in TWS

Raw L2 + tape logging verified

Replay implemented

Fixing crossed book & exception hygiene

Shadow trading pending replay PASS
