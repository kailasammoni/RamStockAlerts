param(
  [ValidateSet("Debug","Release")]
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

dotnet build .\\RamStockAlerts.sln -c $Configuration
dotnet test  .\\RamStockAlerts.sln -c $Configuration

