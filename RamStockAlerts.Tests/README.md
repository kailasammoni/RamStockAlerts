# RamStockAlerts.Tests

End-to-end integration tests for the RamStockAlerts API.

## Overview

This test project contains comprehensive end-to-end tests that verify the complete functionality of the RamStockAlerts API, including:
- Signal creation and retrieval workflows
- Analytics endpoints
- Event replay functionality
- Health checks (liveness and readiness)
- Validation and error handling

## Running the Tests

### Prerequisites
- .NET 10.0 SDK or later

### Run all tests
```bash
dotnet test
```

### Run tests with detailed output
```bash
dotnet test --verbosity detailed
```

### Run specific test
```bash
dotnet test --filter "WhenCreatingSignalThenSignalIsStoredAndRetrievable"
```

## Test Architecture

The tests use:
- **xUnit** as the test framework
- **WebApplicationFactory** for integration testing
- **In-Memory Database** (Entity Framework Core) for data isolation
- Each test gets its own factory instance to ensure complete isolation

### Key Components

- **TestWebApplicationFactory**: Custom factory that configures the application for testing
  - Replaces SQLite/PostgreSQL with in-memory database
  - Sets environment to "Testing"
  - Removes EF Core provider conflicts

- **SignalsEndToEndTests**: Tests for the Signals API
  - Signal creation and retrieval
  - Ticker-specific queries
  - Analytics endpoints
  - Event replay
  - Validation

- **HealthCheckEndToEndTests**: Tests for health check endpoints
  - Liveness probe
  - Readiness probe

## Test Coverage

- ✅ POST /api/signals (create signal)
- ✅ GET /api/signals (retrieve recent signals)
- ✅ GET /api/signals/{ticker} (retrieve signals by ticker)
- ✅ GET /api/signals/analytics/winrate
- ✅ GET /api/signals/analytics/by-hour
- ✅ GET /api/signals/events/replay
- ✅ GET /health/live
- ✅ GET /health/ready
- ✅ Invalid request validation

## Notes

- Tests run in isolation with separate in-memory databases
- No external dependencies required (no real database, no API keys)
- Tests skip database initialization in Program.cs when environment is "Testing"
