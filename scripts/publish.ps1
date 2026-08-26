<#
.SYNOPSIS
    Builds a self-contained Windows release of DaxAlgo Terminal locally — the same artifact the
    Release GitHub Action produces, for testing a release build before tagging.

.PARAMETER Version
    Version stamped into the assemblies and the output folder/zip name. Defaults to 1.0.0.

.PARAMETER Output
    Output root for the published app, zip, and installer. Defaults to C:\DaxAlgoBuild so build
    artifacts never land on the (code-only) repo drive. Override to publish elsewhere.

.PARAMETER Zip
    Also produce a versioned .zip alongside the published folder.

.PARAMETER Installer
    Also build the Inno Setup installer. NOT AVAILABLE IN THIS REPO — the .iss lives in the
    Pro-Installer repo. Kept so the parameter fails with an explanation rather than silently.

.EXAMPLE
    ./scripts/publish.ps1 -Version 1.0.0 -Zip -Installer
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Output  = 'C:\DaxAlgoBuild',
    [switch]$Zip,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $rid   = 'win-x64'
    $stage = Join-Path $Output 'DaxAlgo-Terminal'

    Write-Host "Publishing DaxAlgo Terminal v$Version ($rid)…" -ForegroundColor Cyan

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    # Publish straight into the final layout. Publishing in place avoids duplicating ~430 MB of
    # self-contained output into a separate stage copy.
    #
    # This must stay in step with .github/workflows/release.yml — the whole point of this script is
    # to reproduce the release artifact locally, and a script that builds something else is worse
    # than no script. It pointed at src/TradingTerminal.App and src/TradingTerminal.Backtest.Cli
    # until 2026-08-26: the first is the Professional shell (a different repo), the second was
    # archived with the backtest engine. Neither path had existed for months.
    dotnet publish src/windows/Shell/TradingTerminal.App.Basic/TradingTerminal.App.Basic.csproj `
        -c Release -r $rid --self-contained true -p:Version=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)." }

    # QuestDB is the default store provider and installed builds bundle its runtime. Skipping this
    # gives a build that starts with persistence off — logged, but easy to miss in a smoke test.
    & "$PSScriptRoot/stage-questdb.ps1" -Destination (Join-Path $stage 'questdb')
    if ($LASTEXITCODE -ne 0) { throw "QuestDB staging failed ($LASTEXITCODE)." }

    Copy-Item README.md, CHANGELOG.md, LICENSE $stage -Force

    Write-Host "Published to $stage" -ForegroundColor Green

    if ($Zip) {
        $asset = Join-Path $Output "DaxAlgo-Terminal-v$Version-$rid.zip"
        if (Test-Path $asset) { Remove-Item $asset -Force }
        Compress-Archive -Path "$stage/*" -DestinationPath $asset -Force
        Write-Host "Zipped to $asset" -ForegroundColor Green
    }

    if ($Installer) {
        # The Inno Setup script is NOT in this repo — installer/DaxAlgoTerminal.iss lives in the
        # Pro-Installer repo, which is where installers are built. Saying so beats letting ISCC fail
        # with "file not found" on a path that reads like it should be here.
        if (-not (Test-Path 'installer/DaxAlgoTerminal.iss')) {
            throw ("No installer script in this repo. Installers are built from the Pro-Installer " +
                   "repo (installer/DaxAlgoTerminal.iss). Drop -Installer to publish the portable " +
                   "build, which is what this repo ships.")
        }

        $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
        if (-not $iscc) {
            $candidate = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
            if (Test-Path $candidate) { $iscc = $candidate }
        }
        if (-not $iscc) {
            throw "Inno Setup (iscc) not found. Install it (choco install innosetup) or drop -Installer."
        }

        $stageFull    = (Resolve-Path $stage).Path
        $installerOut = Join-Path $Output 'installer'
        New-Item -ItemType Directory -Force -Path $installerOut | Out-Null
        & $iscc "/DMyAppVersion=$Version" "/DMySourceDir=$stageFull" "/DMyOutputDir=$installerOut" `
                'installer/DaxAlgoTerminal.iss'
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }
        Write-Host "Installer built to $installerOut\DaxAlgo-Terminal-Setup-v$Version.exe" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
