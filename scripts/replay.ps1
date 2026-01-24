param(
  [Parameter(Mandatory = $false)]
  [string]$Symbol = "AAPL",

  [ValidateSet("Debug","Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$env:MODE = "replay"
$env:SYMBOL = $Symbol

# Replay reads the latest matching depth/tape JSONL for the symbol under logs/.
# To replay specific files, set:
#   $env:Replay__DepthFile = "path"
#   $env:Replay__TapeFile  = "path"

dotnet run --project .\\RamStockAlerts.csproj -c $Configuration
