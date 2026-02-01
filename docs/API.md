# REST API Reference

This document describes all REST API endpoints exposed by RamStockAlerts.

## Base URL

```
http://localhost:5000/api
```

In production, replace with your deployed URL.

---

## Admin Operations

Endpoints for system control and health monitoring.

### GET /api/admin/health

Health check endpoint to verify system status.

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2025-01-31T09:30:00Z",
  "version": "1.0.0",
  "components": {
    "database": "healthy",
    "ibkrConnection": "connected",
    "polygonApi": "healthy"
  }
}
```

**Status Codes:**
- `200 OK` - System healthy
- `503 Service Unavailable` - One or more components unhealthy

---

### POST /api/admin/start-trading

Enable live trading mode. System will begin executing trades.

**Request:**
```json
{
  "mode": "live",  // "live" or "shadow"
  "maxDailyDrawdown": 600,
  "maxPositions": 2
}
```

**Response:**
```json
{
  "success": true,
  "message": "Trading enabled in live mode",
  "timestamp": "2025-01-31T09:30:00Z"
}
```

**Status Codes:**
- `200 OK` - Trading enabled successfully
- `400 Bad Request` - Invalid mode or parameters
- `403 Forbidden` - Trading already active

---

### POST /api/admin/stop-trading

Halt trading immediately. Closes open positions and stops signal processing.

**Request:**
```json
{
  "closePositions": true,  // Whether to close existing positions
  "reason": "Daily drawdown limit reached"
}
```

**Response:**
```json
{
  "success": true,
  "message": "Trading halted",
  "positionsClosed": 2,
  "timestamp": "2025-01-31T14:30:00Z"
}
```

**Status Codes:**
- `200 OK` - Trading halted successfully
- `500 Internal Server Error` - Error closing positions

---

### GET /api/admin/config

Retrieve current system configuration.

**Response:**
```json
{
  "mode": "shadow",
  "maxDailyDrawdown": 600,
  "maxPositionsPerSymbol": 1,
  "maxConcurrentPositions": 2,
  "signalScoreThreshold": 8.0,
  "tradingHours": {
    "primaryStart": "09:30",
    "primaryEnd": "10:30",
    "secondaryStart": "10:30",
    "secondaryEnd": "11:30",
    "darkPeriodStart": "11:30",
    "darkPeriodEnd": "14:00",
    "finalStart": "14:00",
    "finalEnd": "15:00"
  }
}
```

**Status Codes:**
- `200 OK` - Configuration retrieved successfully

---

### PUT /api/admin/config

Update system configuration. Requires restart for some settings.

**Request:**
```json
{
  "signalScoreThreshold": 8.5,
  "maxDailyDrawdown": 500
}
```

**Response:**
```json
{
  "success": true,
  "message": "Configuration updated",
  "requiresRestart": false
}
```

**Status Codes:**
- `200 OK` - Configuration updated
- `400 Bad Request` - Invalid configuration values

---

## Execution Operations

Endpoints for manual trade entry and position management.

### POST /api/execution/manual-entry

Submit a manual trade (override automation).

**Request:**
```json
{
  "symbol": "AAPL",
  "action": "BUY",  // "BUY" or "SELL"
  "quantity": 500,
  "orderType": "MARKET",  // "MARKET", "LIMIT", "STOP"
  "limitPrice": 150.50,  // Optional, for LIMIT orders
  "stopPrice": 148.00,   // Optional, for STOP orders
  "profitTarget": 152.00,
  "stopLoss": 149.00,
  "reason": "Manual override - strong setup"
}
```

**Response:**
```json
{
  "success": true,
  "orderId": "1234567890",
  "symbol": "AAPL",
  "quantity": 500,
  "status": "Submitted",
  "timestamp": "2025-01-31T10:15:00Z",
  "estimatedRisk": 75.00,
  "bracketOrders": {
    "entry": "1234567890",
    "profitTarget": "1234567891",
    "stopLoss": "1234567892"
  }
}
```

**Status Codes:**
- `200 OK` - Order submitted successfully
- `400 Bad Request` - Invalid order parameters
- `403 Forbidden` - Trading not enabled or risk limits exceeded
- `500 Internal Server Error` - Order submission failed

---

### GET /api/execution/positions

Retrieve all current open positions.

**Response:**
```json
{
  "positions": [
    {
      "symbol": "AAPL",
      "quantity": 500,
      "entryPrice": 150.00,
      "currentPrice": 151.50,
      "unrealizedPL": 750.00,
      "entryTime": "2025-01-31T10:00:00Z",
      "holdDurationSeconds": 900,
      "profitTarget": 152.00,
      "stopLoss": 149.00,
      "signalScore": 8.7
    },
    {
      "symbol": "TSLA",
      "quantity": 400,
      "entryPrice": 200.00,
      "currentPrice": 199.50,
      "unrealizedPL": -200.00,
      "entryTime": "2025-01-31T10:10:00Z",
      "holdDurationSeconds": 300,
      "profitTarget": 202.50,
      "stopLoss": 198.00,
      "signalScore": 8.2
    }
  ],
  "totalPositions": 2,
  "totalUnrealizedPL": 550.00
}
```

**Status Codes:**
- `200 OK` - Positions retrieved successfully

---

### POST /api/execution/close-position/{symbol}

Close a specific open position immediately.

**Path Parameters:**
- `symbol` - Stock symbol (e.g., "AAPL")

**Request:**
```json
{
  "reason": "Manual close - risk management",
  "orderType": "MARKET"  // "MARKET" or "LIMIT"
}
```

**Response:**
```json
{
  "success": true,
  "symbol": "AAPL",
  "closedQuantity": 500,
  "exitPrice": 151.25,
  "realizedPL": 625.00,
  "commission": 1.00,
  "netPL": 624.00,
  "timestamp": "2025-01-31T10:45:00Z"
}
```

**Status Codes:**
- `200 OK` - Position closed successfully
- `404 Not Found` - No position found for symbol
- `500 Internal Server Error` - Order submission failed

---

### GET /api/execution/orders/{orderId}

Retrieve status of a specific order.

**Path Parameters:**
- `orderId` - IBKR order ID

**Response:**
```json
{
  "orderId": "1234567890",
  "symbol": "AAPL",
  "action": "BUY",
  "quantity": 500,
  "orderType": "MARKET",
  "status": "Filled",
  "submittedAt": "2025-01-31T10:00:00Z",
  "filledAt": "2025-01-31T10:00:02Z",
  "fillPrice": 150.05,
  "commission": 0.50,
  "executionTime": "2.1s"
}
```

**Status Codes:**
- `200 OK` - Order status retrieved
- `404 Not Found` - Order not found

---

## IBKR Scanner Operations

Endpoints for symbol universe scanning and filtering.

### POST /api/scanner/build-universe

Build trading universe for the day (typically called at 09:25 ET).

**Request:**
```json
{
  "filters": {
    "minPrice": 2.00,
    "maxPrice": 20.00,
    "minFloat": 5000000,
    "maxFloat": 50000000,
    "minSpread": 0.02,
    "maxSpread": 0.05,
    "minVolume": 500000
  },
  "maxSymbols": 50
}
```

**Response:**
```json
{
  "success": true,
  "symbolsFound": 42,
  "symbols": [
    {
      "symbol": "AAPL",
      "price": 150.00,
      "float": 15500000,
      "spread": 0.01,
      "volume": 25000000,
      "matchedFilters": true
    },
    // ... more symbols
  ],
  "buildTime": "2025-01-31T09:25:00Z"
}
```

**Status Codes:**
- `200 OK` - Universe built successfully
- `400 Bad Request` - Invalid filter parameters
- `503 Service Unavailable` - IBKR scanner unavailable

---

### GET /api/scanner/universe

Retrieve current trading universe.

**Response:**
```json
{
  "symbols": ["AAPL", "TSLA", "AMD", "NVDA", "..."],
  "count": 42,
  "lastUpdated": "2025-01-31T09:25:00Z"
}
```

**Status Codes:**
- `200 OK` - Universe retrieved successfully

---

### GET /api/scanner/symbol-details/{symbol}

Get detailed information about a specific symbol.

**Path Parameters:**
- `symbol` - Stock symbol (e.g., "AAPL")

**Response:**
```json
{
  "symbol": "AAPL",
  "price": 150.00,
  "bid": 149.99,
  "ask": 150.01,
  "spread": 0.02,
  "spreadPercent": 0.013,
  "float": 15500000,
  "volume": 25000000,
  "vwap": 149.75,
  "lastUpdate": "2025-01-31T10:30:00Z",
  "inUniverse": true,
  "passesFilters": true
}
```

**Status Codes:**
- `200 OK` - Symbol details retrieved
- `404 Not Found` - Symbol not found

---

## Performance Metrics

Endpoints for P&L tracking and performance analysis.

### GET /api/metrics/daily-summary

Get today's performance summary.

**Response:**
```json
{
  "date": "2025-01-31",
  "totalTrades": 6,
  "winningTrades": 4,
  "losingTrades": 2,
  "winRate": 66.67,
  "grossWins": 850.00,
  "grossLosses": 300.00,
  "netPL": 545.00,  // After commissions
  "profitFactor": 2.83,
  "averageWin": 212.50,
  "averageLoss": 150.00,
  "largestWin": 350.00,
  "largestLoss": 200.00,
  "commissions": 5.00,
  "currentDrawdown": 0.00
}
```

**Status Codes:**
- `200 OK` - Summary retrieved successfully

---

### GET /api/metrics/performance/{date}

Get historical performance for a specific date.

**Path Parameters:**
- `date` - Date in YYYY-MM-DD format (e.g., "2025-01-30")

**Response:**
```json
{
  "date": "2025-01-30",
  "totalTrades": 8,
  "winningTrades": 5,
  "losingTrades": 3,
  "winRate": 62.50,
  "grossWins": 950.00,
  "grossLosses": 400.00,
  "netPL": 544.00,
  "profitFactor": 2.38,
  "averageWin": 190.00,
  "averageLoss": 133.33,
  "largestWin": 400.00,
  "largestLoss": 200.00,
  "commissions": 6.00
}
```

**Status Codes:**
- `200 OK` - Performance retrieved successfully
- `404 Not Found` - No data for specified date

---

### GET /api/metrics/trades

Get list of recent trades with filters.

**Query Parameters:**
- `startDate` - Start date (YYYY-MM-DD)
- `endDate` - End date (YYYY-MM-DD)
- `symbol` - Filter by symbol (optional)
- `outcome` - Filter by outcome: "win", "loss" (optional)
- `limit` - Max number of results (default: 100)

**Example:**
```
GET /api/metrics/trades?startDate=2025-01-01&endDate=2025-01-31&symbol=AAPL&limit=50
```

**Response:**
```json
{
  "trades": [
    {
      "id": 1,
      "symbol": "AAPL",
      "entryTime": "2025-01-31T10:00:00Z",
      "exitTime": "2025-01-31T10:15:00Z",
      "entryPrice": 150.00,
      "exitPrice": 151.50,
      "quantity": 500,
      "grossPL": 750.00,
      "commission": 1.00,
      "netPL": 749.00,
      "signalScore": 8.7,
      "holdDurationSeconds": 900,
      "exitReason": "ProfitTarget"
    },
    // ... more trades
  ],
  "totalTrades": 42,
  "returnedTrades": 42
}
```

**Status Codes:**
- `200 OK` - Trades retrieved successfully
- `400 Bad Request` - Invalid date range or parameters

---

### GET /api/metrics/monthly-summary/{year}/{month}

Get monthly performance rollup.

**Path Parameters:**
- `year` - Year (e.g., 2025)
- `month` - Month (1-12)

**Example:**
```
GET /api/metrics/monthly-summary/2025/1
```

**Response:**
```json
{
  "year": 2025,
  "month": 1,
  "totalTrades": 145,
  "winningTrades": 92,
  "losingTrades": 53,
  "winRate": 63.45,
  "grossWins": 18500.00,
  "grossLosses": 7950.00,
  "netPL": 10420.00,
  "profitFactor": 2.33,
  "averageWin": 201.09,
  "averageLoss": 150.00,
  "maxDrawdown": 1200.00,
  "bestDay": {
    "date": "2025-01-15",
    "netPL": 1250.00
  },
  "worstDay": {
    "date": "2025-01-08",
    "netPL": -450.00
  },
  "tradingDays": 21
}
```

**Status Codes:**
- `200 OK` - Monthly summary retrieved
- `404 Not Found` - No data for specified month

---

### GET /api/metrics/profit-factor-trend

Get profit factor trend over time.

**Query Parameters:**
- `days` - Number of days to include (default: 30)

**Response:**
```json
{
  "dataPoints": [
    {
      "date": "2025-01-01",
      "profitFactor": 2.10,
      "cumulativeProfitFactor": 2.10
    },
    {
      "date": "2025-01-02",
      "profitFactor": 2.45,
      "cumulativeProfitFactor": 2.27
    },
    // ... more data points
  ],
  "currentProfitFactor": 2.33,
  "trend": "improving"  // "improving", "declining", "stable"
}
```

**Status Codes:**
- `200 OK` - Trend data retrieved successfully

---

## Signals (Internal/Debug)

These endpoints are typically used for debugging and analysis.

### GET /api/signals/recent

Get recent signal scores (including rejected signals).

**Query Parameters:**
- `limit` - Max number of signals (default: 100)
- `includeRejected` - Include rejected signals (default: false)

**Response:**
```json
{
  "signals": [
    {
      "timestamp": "2025-01-31T10:30:00Z",
      "symbol": "AAPL",
      "score": 8.7,
      "accepted": true,
      "components": {
        "bidAskRatio": 2.5,
        "spreadCompression": 1.2,
        "tapeVelocity": 1.8,
        "level2Depth": 2.0,
        "vwapReclaim": 1.2
      },
      "gateResults": [
        {"gate": "HardGate", "result": "pass"},
        {"gate": "AbsorptionReject", "result": "pass"},
        {"gate": "SpreadGate", "result": "pass"}
      ]
    },
    // ... more signals
  ]
}
```

**Status Codes:**
- `200 OK` - Signals retrieved successfully

---

## Error Responses

All endpoints return consistent error responses:

```json
{
  "success": false,
  "error": {
    "code": "INVALID_PARAMETER",
    "message": "Signal score threshold must be between 0 and 10",
    "details": {
      "parameter": "signalScoreThreshold",
      "providedValue": 15.0
    }
  },
  "timestamp": "2025-01-31T10:30:00Z"
}
```

### Common Error Codes

| Code | HTTP Status | Description |
|------|-------------|-------------|
| `INVALID_PARAMETER` | 400 | Invalid request parameter |
| `TRADING_DISABLED` | 403 | Trading not enabled |
| `RISK_LIMIT_EXCEEDED` | 403 | Risk check failed |
| `POSITION_NOT_FOUND` | 404 | Position does not exist |
| `ORDER_NOT_FOUND` | 404 | Order does not exist |
| `IBKR_CONNECTION_ERROR` | 503 | IBKR TWS not connected |
| `DATABASE_ERROR` | 500 | Database operation failed |
| `EXECUTION_ERROR` | 500 | Order execution failed |

---

## Authentication

**Note:** Authentication is not yet implemented. All endpoints are currently open.

**Planned:** Bearer token authentication for production deployment.

```http
Authorization: Bearer <token>
```

---

## Rate Limiting

**Current:** No rate limiting implemented.

**Planned:**
- Admin endpoints: 10 requests/minute
- Execution endpoints: 20 requests/minute
- Metrics endpoints: 100 requests/minute

---

## WebSocket API (Future)

**Planned:** Real-time signal and position updates via WebSocket.

```
ws://localhost:5000/ws/signals
ws://localhost:5000/ws/positions
```

---

## Related Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - System architecture and component design
- [GLOSSARY.md](GLOSSARY.md) - Trading terminology
- [EXAMPLES.md](EXAMPLES.md) - Example API usage scenarios
