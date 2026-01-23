# AGENTS.md - Guide for Jules and other AI Agents

## Overview
RamStockAlerts is an order-flow intelligence platform built with .NET 10. It detects liquidity dislocations in equity markets using Level II market depth and tape data.

## Tech Stack
- **Framework**: .NET 10 (ASP.NET Core)
- **Database**: SQLite (default) or PostgreSQL
- **Key Libraries**: Entity Framework Core, Serilog, Alpaca.Markets, IBApi

## Project Structure
- `RamStockAlerts/`: Main API project and core logic.
- `RamStockAlerts.Execution/`: Module for order execution and risk management.
- `RamStockAlerts.Tests/`: Unit and integration tests.
- `RamStockAlerts.Execution.Tests/`: Tests for the execution module.

## Setup Requirements
Jules executes in an Ubuntu environment. The following steps are required:
1. **.NET 10 SDK**: Must be installed.
2. **Setup Script**: Run `./setup-jules.sh` to initialize the environment.

## Build and Test Commands
- **Restore**: `dotnet restore`
- **Build**: `dotnet build`
- **Test**: `dotnet test`

## Key Design Patterns
- **Shadow Trading**: The system default mode is `Shadow`, which generates trade blueprints without execution.
- **Microstructure Analysis**: Logic for Bid-Ask imbalance, Tape Velocity, and Spread Analysis is located in `Engine/` and `Services/ShadowTradingCoordinator.cs`.
- **Deterministic Replay**: The system can replay JSONL market data for backtesting.

## Coding Conventions
- Use C# 12+ features.
- Structured logging with Serilog.
- Prefer `required` members for models.
- Core business logic should be in Services or Engine modules.

## Known Challenges for Agents
- **IBKR API**: Requires a running TWS/Gateway instance for live data. For agent tasks, focus on unit tests or data replay mode.
- **SQLite**: Ensure `ramstockalerts.db` is initialized via `dotnet ef database update` or `EnsureCreated()`.
