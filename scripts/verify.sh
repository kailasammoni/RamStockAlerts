#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${1:-Release}"

dotnet build ./RamStockAlerts.sln -c "$CONFIGURATION"
dotnet test  ./RamStockAlerts.sln -c "$CONFIGURATION"

