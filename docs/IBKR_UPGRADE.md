# IBKR CSharpClient Upgrade

Use this whenever a new TWS API release drops.

## Update the client

PowerShell:
```powershell
scripts\update-ibkr-client.ps1 "C:\Path\To\TWS API\source\CSharpClient\client"
```

Bash:
```bash
scripts/update-ibkr-client.sh "/path/to/TWS API/source/CSharpClient/client"
```

Both scripts keep a timestamped backup of the prior `lib/ibkr/CSharpClient`.

## Verify and fix signatures

1) Build and tests:
```powershell
powershell -File scripts\verify.ps1
```

2) Fix compile errors caused by IBKR API changes:
- Check all classes inheriting `DefaultEWrapper`.
- Update overridden method signatures to match the new IBApi.
- If new protobuf message types appear, add no-op handlers in `IbkrRecorderHostedService` if needed.

3) Validate runtime:
- Connect to TWS/Gateway and verify market data, orders, and scanner flows.
