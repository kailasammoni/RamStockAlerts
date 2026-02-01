# RamStockAlerts

Order-flow intelligence system for scalping stocks. Detects liquidity dislocations and generates 6-9 high-probability trades per day targeting $700-$1,000/week profit on a $30-$80K account.

## What This Does

RamStockAlerts is a .NET-based automated trading system that:
- Monitors Level 2 order book data from Interactive Brokers (IBKR)
- Identifies liquidity dislocations using order flow analysis
- Scores trading opportunities on a 0-10 scale (8.0+ threshold for alerts)
- Executes bracket orders with profit targets and stop losses
- Tracks performance metrics and P&L across trading sessions

**Key Performance Targets:**
- Profit Factor: ≥ 2.0 (minimum), target 2.5
- Win Rate: ≥ 60%
- Average Net Win: $80-$100 per trade
- Max Daily Drawdown: $600 (1.5% of account)
- Max Monthly Drawdown: < $3,000 (7.5%)

## Key Components

```
src/RamStockAlerts/
├── Controllers/          # REST API endpoints
│   ├── AdminController              # System control (start/stop trading)
│   ├── ExecutionController          # Manual trades and position management
│   ├── IBKRScannerController        # Symbol universe scanning
│   └── PerformanceMetricsController # P&L and performance tracking
│
├── Services/            # Core business logic
│   ├── OrderFlowSignalService       # Signal scoring (0-10 scale)
│   ├── DepthDeltaTracker           # Level 2 order book monitoring
│   ├── DepthUniverseFilter         # Real-time symbol filtering
│   ├── OutcomeTracker              # Trade result logging
│   └── PerformanceMetricsAggregator # Daily/weekly rollup
│
├── Engine/             # Signal validation and scoring
│   ├── GateTrace                   # Validation gate pipeline
│   ├── AbsorptionReject            # Detect fake bid walls
│   ├── HardGate                    # Hard rejection rules
│   └── SignalValidationEngine      # Orchestrates all gates
│
├── Feeds/              # Market data ingestion
│   └── IBKRClient                  # Interactive Brokers API wrapper
│
├── Execution/          # Order execution module (separate project)
│   ├── ExecutionService            # Order submission and management
│   ├── RiskManager                 # Position sizing and risk checks
│   ├── BracketTemplateBuilder      # Profit target + stop loss orders
│   └── ExecutionLedger             # Trade history storage
│
├── Models/             # Data models
│   ├── OrderBook                   # Level 2 depth snapshot
│   ├── TradeOutcome                # P&L record for completed trade
│   └── DailyPerformanceSnapshot    # Daily metrics aggregate
│
└── Universe/           # Trading universe management
    └── SymbolFilter                # Price, float, spread filters
```

## Trading Logic Overview

### 1. Universe Selection (09:25 ET)
Filter symbols by:
- Price: $2-$20 per share
- Float: 5M-50M shares
- Spread: $0.02-$0.05 (tight enough for scalping)
- Minimum volume and liquidity thresholds

### 2. Signal Scoring (Real-time)
Each opportunity scored 0-10 based on:
- **Bid-Ask Ratio**: Strong bid pressure vs ask pressure
- **Spread Compression**: Tightening spread indicates accumulation
- **Tape Velocity**: Speed of trades printing (Time & Sales)
- **Level 2 Depth**: Size and stability of bid/ask walls
- **VWAP Reclaim**: Price crossing back above VWAP with momentum

### 3. Entry Validation (8+ Gates)
Reject signals that show manipulation:
- Flash walls (large orders that disappear quickly)
- Iceberg selling (hidden large sell orders)
- Spread blowouts (sudden liquidity evaporation)
- Low float pumps (coordinated manipulation)
- Absorption fakeouts (fake bid walls)

**Minimum Score:** 8.0/10 to generate alert

### 4. Position Sizing
- **Shares:** 400-600 per position
- **Risk:** 0.25% of account per trade ($75-$100 on $40K)
- **Max Positions:** 1-2 concurrent positions
- **Max Trades/Day:** 6-9 trades

### 5. Exit Strategy
Bracket orders with:
- **Profit Target:** Based on signal strength and volatility
- **Stop Loss:** Risk-defined based on entry structure
- **Time Exit:** Close positions before market close

## Trading Schedule (Eastern Time)

| Time | Phase | Max Trades | Notes |
|------|-------|-----------|-------|
| 09:25 | Universe Build | - | Filter symbols for the day |
| 09:30-10:30 | Primary Window | 3 | Highest probability setups |
| 10:30-11:30 | Secondary Window | 2 | Selective continuation trades |
| 11:30-14:00 | Dark Period | 0 | No trading (low volume, chop) |
| 14:00-15:00 | Final Window | 2 | Conditional (only if clear setup) |

## Quick Start

### Prerequisites
- .NET 10 SDK
- Interactive Brokers TWS or IB Gateway (running on localhost:7497)
- SQL Server or PostgreSQL database
- Polygon.io API key (market data)
- Alpaca API credentials (optional, for paper trading)

### Configuration
1. Copy configuration template:
   ```bash
   cp appsettings.json appsettings.Development.json
   ```

2. Set environment variables (see `.env.example` for template):
   ```bash
   export IBKR_HOST=127.0.0.1
   export IBKR_PORT=7497
   export IBKR_CLIENT_ID=1
   export POLYGON_API_KEY=your_key_here
   export ALPACA_KEY=your_key_here
   export ALPACA_SECRET=your_secret_here
   ```

3. Run database migrations:
   ```bash
   dotnet ef database update --project src/RamStockAlerts
   ```

### Running the Application

**Live Trading Mode:**
```bash
export MODE=live
dotnet run --project ./src/RamStockAlerts/RamStockAlerts.csproj -c Release
```

**Shadow Mode (observe without executing):**
```bash
export MODE=shadow
dotnet run --project ./src/RamStockAlerts/RamStockAlerts.csproj -c Release
```

**Replay Mode (backtest specific symbol):**
```bash
export MODE=replay
export SYMBOL=AAPL
dotnet run --project ./src/RamStockAlerts/RamStockAlerts.csproj -c Release
```

Or use the convenience scripts:
```bash
./scripts/replay.sh Release    # Windows: .\scripts\replay.ps1 Release
```

### Running Tests
```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/RamStockAlerts.Tests
dotnet test tests/RamStockAlerts.Execution.Tests

# Run with code coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## API Endpoints

Access the system via REST API:
- `GET /api/admin/health` - System health check
- `POST /api/admin/start-trading` - Enable live trading
- `POST /api/admin/stop-trading` - Halt trading
- `GET /api/metrics/daily-summary` - Today's P&L and win rate

See [docs/API.md](docs/API.md) for complete endpoint documentation.

## Architecture

The system follows a layered architecture:
- **Controllers** → Handle HTTP requests
- **Services** → Business logic and orchestration
- **Engine** → Signal validation and scoring
- **Feeds** → Market data ingestion from IBKR
- **Execution** → Order management and risk control
- **Models** → Data structures and entities

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for detailed system design.

## Documentation

- [ARCHITECTURE.md](docs/ARCHITECTURE.md) - System design and component relationships
- [API.md](docs/API.md) - REST API endpoint reference
- [GLOSSARY.md](docs/GLOSSARY.md) - Trading terminology and system concepts
- [DATA_FLOW.md](docs/DATA_FLOW.md) - Data flow diagrams and database schema
- [EXAMPLES.md](docs/EXAMPLES.md) - Real-world trading examples and scenarios
- [ProductGoalV2.md](ProductGoalV2.md) - Strategic product goals and specifications

## Project Structure

```
RamStockAlerts/
├── src/                          # Source code
│   ├── RamStockAlerts/          # Main web service
│   └── RamStockAlerts.Execution/ # Order execution module
├── tests/                        # Unit and integration tests
│   ├── RamStockAlerts.Tests/
│   └── RamStockAlerts.Execution.Tests/
├── lib/ibkr/                     # IBKR API client library (vendored)
├── scripts/                      # Build and deployment scripts
├── docs/                         # Documentation
└── OpenAI/                       # Experimental OpenAI integration
```

## Technology Stack

- **Runtime:** .NET 10 (C# 13)
- **Web Framework:** ASP.NET Core
- **Database:** Entity Framework Core (SQL Server / PostgreSQL)
- **Market Data:** Interactive Brokers TWS API, Polygon.io
- **Testing:** xUnit, Coverlet (code coverage)
- **Broker Integration:** Interactive Brokers, Alpaca (paper trading)

## Success Criteria (90-Day Validation)

The system will be validated over 90 days with these targets:

✅ **Pass:** Profit Factor ≥ 2.0, Win Rate ≥ 60%, Max Monthly DD < $3,000
⚠️ **Review:** Profit Factor 1.5-2.0, adjust parameters
❌ **Abort:** Profit Factor < 1.5 after 100 trades

## Safety Features

- **Max Daily Drawdown:** System auto-halts at $600 loss/day
- **Position Size Limits:** 400-600 shares (prevents moving market)
- **Manipulation Detection:** 8+ validation gates reject fake signals
- **Risk Per Trade:** Hard-capped at 0.25% of account
- **Time-Based Restrictions:** No trading during low-liquidity periods

## Development

### Build
```bash
dotnet build RamStockAlerts.sln
```

### Code Style
The project uses:
- C# 13 with nullable reference types enabled
- Implicit usings
- EditorConfig for formatting consistency

### Contributing
See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines (TODO).

## License

Proprietary - All rights reserved (TODO: Add license file)

## Support

For issues or questions, see:
- System architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- Trading terminology: [docs/GLOSSARY.md](docs/GLOSSARY.md)
- API reference: [docs/API.md](docs/API.md)

---

**⚠️ Trading Disclaimer:** This software is for educational and research purposes. Trading stocks involves substantial risk of loss. Past performance does not guarantee future results.
