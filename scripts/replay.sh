#!/usr/bin/env bash
set -euo pipefail

SYMBOL="${SYMBOL:-AAPL}"
CONFIGURATION="${1:-Release}"

export MODE="replay"
export SYMBOL="$SYMBOL"

dotnet run --project ./src/RamStockAlerts/RamStockAlerts.csproj -c "$CONFIGURATION"
