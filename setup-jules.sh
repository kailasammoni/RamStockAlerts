#!/bin/bash
# setup-jules.sh - Environment setup for Jules (Ubuntu)

set -e

echo "--- Starting RamStockAlerts Setup for Jules ---"

# 1. Install .NET 10 SDK if not present
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET 10 SDK..."
    # Using Microsoft's install script
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --version 10.0.100
    export DOTNET_ROOT=$HOME/.dotnet
    export PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools
else
    echo "Found .NET Version: $(dotnet --version)"
fi

# 2. Add dotnet tools to PATH
export PATH="$PATH:$HOME/.dotnet/tools"

# 3. Restore Dependencies
echo "Restoring NuGet packages..."
dotnet restore

# 4. Install Entity Framework Tools if not present
if ! dotnet tool list -g | grep -q "dotnet-ef"; then
    echo "Installing dotnet-ef global tool..."
    dotnet tool install --global dotnet-ef --version 10.0.1
fi

# 5. Initialize Database
echo "Initializing SQLite database..."
# If database doesn't exist, create it via EF migrations
if [ ! -f "ramstockalerts.db" ]; then
    dotnet ef database update
else
    echo "Database already exists."
fi

# 6. Final Build Verification
echo "Verifying build..."
dotnet build --no-restore

echo "--- Setup Complete! ---"
