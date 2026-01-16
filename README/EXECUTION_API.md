# Execution API - Manual Testing

## Configuration

The execution subsystem can be enabled/disabled via configuration in [appsettings.json](../appsettings.json):

```json
{
  "Execution": {
    "Enabled": false,
    "Broker": "Fake",
    "MaxNotionalUsd": 2000,
    "MaxShares": 500
  }
}
```

**Configuration Options:**

- `Execution:Enabled` (bool, default: `false`) - Enables the execution subsystem. When `false`, all endpoints return `503 Service Unavailable`.
- `Execution:Broker` (string, default: `"Fake"`) - Selects the broker implementation:
  - `"Fake"` - FakeBrokerClient (always succeeds, generates mock order IDs)
  - `"IBKR"` - Interactive Brokers client (not yet implemented, falls back to Fake)
- `Execution:MaxNotionalUsd` (decimal, default: `2000`) - Maximum dollar value per order
- `Execution:MaxShares` (decimal, default: `500`) - Maximum share quantity per order

**Example: Enable Execution with Fake Broker**
```json
{
  "Execution": {
    "Enabled": true,
    "Broker": "Fake",
    "MaxNotionalUsd": 5000,
    "MaxShares": 1000
  }
}
```

---

## Endpoints

### 1. POST /api/execution/order
Place a single order.

**Request Body:**
```json
{
  "mode": "Paper",
  "symbol": "AAPL",
  "side": "Buy",
  "type": "Market",
  "quantity": 100,
  "tif": "Day"
}
```

**Response (200 OK):**
```json
{
  "status": "Accepted",
  "rejectionReason": null,
  "brokerName": "FakeBroker",
  "brokerOrderIds": [
    "FAKE-a1b2c3d4e5f6..."
  ],
  "timestampUtc": "2026-01-14T12:34:56.789Z",
  "debug": "Fake order placed: AAPL Buy 100"
}
```

---

### 2. POST /api/execution/bracket
Place a bracket order (entry + stop-loss + take-profit).

**Request Body:**
```json
{
  "entry": {
    "mode": "Paper",
    "symbol": "TSLA",
    "side": "Buy",
    "type": "Limit",
    "quantity": 50,
    "limitPrice": 200.00
  },
  "stopLoss": {
    "mode": "Paper",
    "symbol": "TSLA",
    "side": "Sell",
    "type": "Stop",
    "quantity": 50,
    "stopPrice": 190.00
  },
  "takeProfit": {
    "mode": "Paper",
    "symbol": "TSLA",
    "side": "Sell",
    "type": "Limit",
    "quantity": 50,
    "limitPrice": 210.00
  }
}
```

**Response (200 OK):**
```json
{
  "status": "Accepted",
  "rejectionReason": null,
  "brokerName": "FakeBroker",
  "brokerOrderIds": [
    "FAKE-ENTRY-abc123...",
    "FAKE-STOP-def456...",
    "FAKE-PROFIT-ghi789..."
  ],
  "timestampUtc": "2026-01-14T12:35:01.234Z",
  "debug": "Fake bracket placed: TSLA (Entry + Stop + TP)"
}
```

---

### 3. GET /api/execution/ledger
Query recent execution history.

**Response (200 OK):**
```json
{
  "intents": [
    {
      "intentId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "decisionId": null,
      "mode": "Paper",
      "symbol": "AAPL",
      "side": "Buy",
      "type": "Market",
      "quantity": 100,
      "notionalUsd": null,
      "limitPrice": null,
      "stopPrice": null,
      "tif": "Day",
      "createdUtc": "2026-01-14T12:34:56.789Z",
      "tags": null
    }
  ],
  "brackets": [
    {
      "entry": { ... },
      "takeProfit": { ... },
      "stopLoss": { ... },
      "ocoGroupId": null
    }
  ],
  "results": [
    {
      "status": "Accepted",
      "rejectionReason": null,
      "brokerName": "FakeBroker",
      "brokerOrderIds": ["FAKE-abc123..."],
      "timestampUtc": "2026-01-14T12:34:56.890Z",
      "debug": "Fake order placed: AAPL Buy 100"
    }
  ]
}
```

---

## Risk Rejection Examples

### Order Rejected (Exceeds Max Shares)
**Request:**
```json
{
  "mode": "Paper",
  "symbol": "NVDA",
  "side": "Buy",
  "type": "Market",
  "quantity": 5000
}
```

**Response (200 OK but Rejected):**
```json
{
  "status": "Rejected",
  "rejectionReason": "Quantity 5000 exceeds maximum 500",
  "brokerName": "FakeBroker",
  "brokerOrderIds": [],
  "timestampUtc": "2026-01-14T12:36:00.123Z",
  "debug": null
}
```

### Bracket Rejected (Live Mode Without Stop-Loss)
**Request:**
```json
{
  "entry": {
    "mode": "Live",
    "symbol": "GOOG",
    "side": "Buy",
    "type": "Limit",
    "quantity": 25,
    "limitPrice": 100.00
  },
  "stopLoss": null,
  "takeProfit": {
    "mode": "Live",
    "symbol": "GOOG",
    "side": "Sell",
    "type": "Limit",
    "quantity": 25,
    "limitPrice": 110.00
  }
}
```

**Response (200 OK but Rejected):**
```json
{
  "status": "Rejected",
  "rejectionReason": "StopLoss is required in Live mode",
  "brokerName": "FakeBroker",
  "brokerOrderIds": [],
  "timestampUtc": "2026-01-14T12:37:00.456Z",
  "debug": null
}
```

---

## Error Responses

### Execution Disabled (503 Service Unavailable)
When `Execution:Enabled` is `false` in configuration, all endpoints return:

**Response (503):**
```json
{
  "error": "Execution module is disabled. Set Execution:Enabled=true in configuration to enable."
}
```

---

## Testing with curl

```bash
# Place a simple order
curl -X POST http://localhost:5000/api/execution/order \
  -H "Content-Type: application/json" \
  -d '{
    "mode": "Paper",
    "symbol": "AAPL",
    "side": "Buy",
    "type": "Market",
    "quantity": 100
  }'

# Place a bracket order
curl -X POST http://localhost:5000/api/execution/bracket \
  -H "Content-Type: application/json" \
  -d '{
    "entry": {
      "mode": "Paper",
      "symbol": "TSLA",
      "side": "Buy",
      "type": "Limit",
      "quantity": 50,
      "limitPrice": 200.00
    },
    "stopLoss": {
      "mode": "Paper",
      "symbol": "TSLA",
      "side": "Sell",
      "type": "Stop",
      "quantity": 50,
      "stopPrice": 190.00
    },
    "takeProfit": {
      "mode": "Paper",
      "symbol": "TSLA",
      "side": "Sell",
      "type": "Limit",
      "quantity": 50,
      "limitPrice": 210.00
    }
  }'

# Query the ledger
curl http://localhost:5000/api/execution/ledger
```

---

## Notes

- **Configuration-Based Enabling**: Execution subsystem is disabled by default (`Execution:Enabled=false`). Set to `true` to enable endpoints.
- **Broker Selection**: `Execution:Broker` controls which broker implementation to use (`"Fake"` or `"IBKR"`). Currently only FakeBrokerClient is fully implemented.
- **FakeBrokerClient**: Always succeeds with mock order IDs (format: `FAKE-{guid}`)
- **Risk Validation**: Orders are validated before broker placement (symbol required, quantity/notional specified, within limits)
- **Live Mode**: Bracket orders in Live mode MUST include a stop-loss
- **Notional Sizing**: Currently unsupported (F2 limitation); only quantity-based orders accepted
- **Future**: Real IBKR broker integration in progress
