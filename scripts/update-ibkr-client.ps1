param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [string]$Destination = "lib/ibkr/CSharpClient"
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcPath = Resolve-Path -Path $Source -ErrorAction Stop
$destPath = Join-Path $repoRoot $Destination
$backupPath = "$destPath.bak.$(Get-Date -Format yyyyMMddHHmmss)"

if (-not (Test-Path -Path (Join-Path $srcPath "CSharpAPI.csproj"))) {
    Write-Warning "CSharpAPI.csproj not found under $srcPath. Make sure you passed the 'CSharpClient' folder."
}

if (Test-Path -Path $destPath) {
    Move-Item -Path $destPath -Destination $backupPath -Force
}

try {
    New-Item -ItemType Directory -Path $destPath -Force | Out-Null

    $excludeDirs = @("bin", "obj", ".git", ".vs")
    $robocopyArgs = @(
        $srcPath,
        $destPath,
        "/MIR",
        "/XD"
    ) + $excludeDirs + @(
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NC",
        "/NS",
        "/NP"
    )

    & robocopy @robocopyArgs | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }

    Write-Host "IBKR CSharpClient updated at $destPath"
    if (Test-Path -Path $backupPath) {
        Write-Host "Backup kept at $backupPath"
    }
}
catch {
    if (Test-Path -Path $destPath) {
        Remove-Item -Path $destPath -Recurse -Force
    }
    if (Test-Path -Path $backupPath) {
        Move-Item -Path $backupPath -Destination $destPath -Force
    }
    throw
}
