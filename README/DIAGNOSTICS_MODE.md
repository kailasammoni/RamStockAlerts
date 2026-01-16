# Subscription Diagnostics Mode

## Purpose

The subscription diagnostics mode helps determine why a symbol might be "dead" or not receiving market data. It systematically tests subscriptions and provides a clear analysis of the issue.

## Common Issues Diagnosed

1. **Symbol not trading** - No market activity
2. **Exchange routing wrong** - Symbol works on SMART but not primary exchange
3. **Entitlement missing** - Subscription permission not granted
4. **Tick-by-tick not enabled** - Deep market data subscription required
5. **Mapping bug** - Contract classification or symbol mapping issue

## Usage

### 1. Configure Symbols

Create or edit `appsettings.Diagnostics.json`:

```json
{
  "MODE": "diagnostics",
  "Diagnostics": {
    "Symbols": "AAPL,MSFT,GOOGL,BIYA,SPY"
  },
  "Ibkr": {
    "Host": "127.0.0.1",
    "Port": 7497,
    "ClientId": 99
  }
}
```

Or set via environment variable:

```bash
export MODE=diagnostics
export Diagnostics__Symbols="AAPL,MSFT,GOOGL,BIYA,SPY"
```

### 2. Run Diagnostics

```bash
# Using appsettings file
dotnet run --environment Diagnostics

# Or with env vars
MODE=diagnostics Diagnostics__Symbols="AAPL,BIYA,SPY" dotnet run
```

### 3. Read Results

The tool will output a table showing:

```
Symbol   | Primary  | Used     | L1?  | Tape? | Depth? | L1Cnt | TapeCnt | FirstMs  | AgeMs    | Errors
-------------------------------------------------------------------------------------------------------------------------
AAPL     | NASDAQ   | SMART    | YES  | YES   | NO     | 150   | 75      | 12345678 | 125      | None
BIYA     | NYSE     | SMART    | NO   | NO    | NO     | 0     | 0       | N/A      | N/A      | None
MSFT     | NASDAQ   | SMART    | YES  | YES   | NO     | 140   | 68      | 12345680 | 130      | None
```

Followed by analysis:

```
ANALYSIS:
  AAPL: Working on SMART - L1: 150, Tape: 75
  BIYA: No data even on SMART - symbol not trading or entitlement missing
  MSFT: Working on SMART - L1: 140, Tape: 68
```

## How It Works

1. **Read Symbols**: Reads up to 10 symbols from configuration
2. **Test Primary Exchange**: 
   - Subscribes to L1 market data and tick-by-tick on the symbol's primary exchange
   - Waits 15 seconds to collect receipts
   - Records any IBKR error codes
3. **Fallback to SMART**: 
   - If primary exchange received no data, retries with SMART routing
   - Waits another 15 seconds
4. **Analysis**: Correlates results with known error codes to provide actionable diagnosis

## Error Code Reference

Common IBKR error codes you might see:

- **10092**: Deep market data not enabled (need Level II subscription)
- **10167**: Tick-by-tick subscription limit reached (max concurrent subscriptions)
- **200**: No security definition found (invalid symbol or contract)
- **354**: Requested market data not subscribed (entitlement missing)

## Example Scenarios

### Scenario 1: BIYA Dead Symbol

```
Symbol   | Primary  | Used     | L1?  | Tape? | Errors
--------------------------------------------------------
BIYA     | NYSE     | NYSE     | NO   | NO    | None
BIYA     | NYSE     | SMART    | NO   | NO    | None

ANALYSIS: No data even on SMART - symbol not trading or entitlement missing
```

**Diagnosis**: Symbol genuinely not trading or suspended.

### Scenario 2: Exchange Routing Issue

```
Symbol   | Primary  | Used     | L1?  | Tape? | Errors
--------------------------------------------------------
XYZ      | NASDAQ   | NASDAQ   | NO   | NO    | None
XYZ      | NASDAQ   | SMART    | YES  | YES   | None

ANALYSIS: Working on SMART - L1: 120, Tape: 45
```

**Diagnosis**: Primary exchange routing not working, but SMART routing succeeds. Use SMART for this symbol.

### Scenario 3: Entitlement Issue

```
Symbol   | Primary  | Used     | L1?  | Tape? | Errors
--------------------------------------------------------
AAPL     | NASDAQ   | SMART    | YES  | NO    | 10092

ANALYSIS: Deep market data not enabled (error 10092)
```

**Diagnosis**: Need to enable Level II / deep market data subscription in IBKR account.

### Scenario 4: Tick-by-Tick Limit

```
Symbol   | Primary  | Used     | L1?  | Tape? | Errors
--------------------------------------------------------
MSFT     | NASDAQ   | SMART    | YES  | NO    | 10167

ANALYSIS: Tick-by-tick subscription limit reached (error 10167)
```

**Diagnosis**: Already at maximum concurrent tick-by-tick subscriptions. Need to cancel some before adding more.

## Integration with Main Application

After running diagnostics, you can:

1. **Update Configuration**: Remove dead symbols from candidate lists
2. **Fix Exchange Routing**: Update contract classifications to use SMART instead of primary
3. **Enable Entitlements**: Contact IBKR to enable required market data subscriptions
4. **Adjust Limits**: Reduce concurrent subscriptions if hitting limits

## Testing

Run the diagnostic tests:

```bash
dotnet test --filter FullyQualifiedName~SubscriptionDiagnostics
```

The tests verify the analysis logic for various scenarios:
- No data on primary vs SMART
- Error code detection (10092, 10167, 200)
- L1 without tape scenarios
- Working subscriptions

## Configuration Reference

```json
{
  "Diagnostics": {
    "Symbols": "AAPL,MSFT,..."  // Comma-separated, max 10 symbols
  },
  "Ibkr": {
    "Host": "127.0.0.1",         // TWS/Gateway host
    "Port": 7497,                 // Paper: 7497, Live: 7496
    "ClientId": 99                // Unique client ID
  }
}
```

Environment variables override config file:
- `MODE=diagnostics`
- `Diagnostics__Symbols="AAPL,BIYA,SPY"`
- `Ibkr__Host=127.0.0.1`
- `Ibkr__Port=7497`
- `Ibkr__ClientId=99`
