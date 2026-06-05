<#
.SYNOPSIS
    Builds a self-contained Windows release of DaxAlgo Terminal locally — the same artifact the
    Release GitHub Action produces, for testing a release build before tagging.

.PARAMETER Version
    Version stamped into the assemblies and the output folder/zip name. Defaults to 1.0.0.

.PARAMETER Output
    Output root. Defaults to .\publish.

.PARAMETER Zip
    Also produce a versioned .zip alongside the published folder.

.PARAMETER Installer
    Also build the Inno Setup installer (requires iscc on PATH or Inno Setup 6 installed).

.EXAMPLE
    ./scripts/publish.ps1 -Version 1.0.0 -Zip -Installer
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Output  = 'publish',
    [switch]$Zip,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $rid     = 'win-x64'
    $appOut  = Join-Path $Output 'app'
    $cliOut  = Join-Path $Output 'cli'
    $stage   = Join-Path $Output 'DaxAlgo-Terminal'

    Write-Host "Publishing DaxAlgo Terminal v$Version ($rid)…" -ForegroundColor Cyan

    dotnet publish src/TradingTerminal.App/TradingTerminal.App.csproj `
        -c Release -r $rid --self-contained true -p:Version=$Version -o $appOut
    if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)." }

    dotnet publish src/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj `
        -c Release -r $rid --self-contained true -p:Version=$Version -o $cliOut
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed ($LASTEXITCODE)." }

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item "$appOut/*" $stage -Recurse -Force
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'cli') | Out-Null
    Copy-Item "$cliOut/*" (Join-Path $stage 'cli') -Recurse -Force
    Copy-Item README.md, CHANGELOG.md, LICENSE $stage -Force

    Write-Host "Published to $stage" -ForegroundColor Green

    if ($Zip) {
        $asset = Join-Path $Output "DaxAlgo-Terminal-v$Version-$rid.zip"
        if (Test-Path $asset) { Remove-Item $asset -Force }
        Compress-Archive -Path "$stage/*" -DestinationPath $asset -Force
        Write-Host "Zipped to $asset" -ForegroundColor Green
    }

    if ($Installer) {
        $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
        if (-not $iscc) {
            $candidate = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
            if (Test-Path $candidate) { $iscc = $candidate }
        }
        if (-not $iscc) {
            throw "Inno Setup (iscc) not found. Install it (choco install innosetup) or drop -Installer."
        }

        $stageFull = (Resolve-Path $stage).Path
        & $iscc "/DMyAppVersion=$Version" "/DMySourceDir=$stageFull" 'installer/DaxAlgoTerminal.iss'
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
        Write-Host "Installer built to installer/Output/DaxAlgo-Terminal-Setup-v$Version.exe" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
