# Builds and packages the canonical engine as a deterministic .daxstrategy. When the scaffold includes
# --ui, the Windows assembly is added as the optional presentation companion.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$name = "DaxNewStrategy"
$project = Join-Path $PSScriptRoot "$name\$name.csproj"
$manifestPath = Join-Path $PSScriptRoot "$name\plugin.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$bin = Join-Path $PSScriptRoot "$name\bin\$Configuration\net9.0-windows7.0"
$engineBin = Join-Path $PSScriptRoot "$name\Engine\bin\$Configuration\net9.0-windows7.0"
$engine = Join-Path $bin "$name.Engine.dll"
$windows = Join-Path $bin "$name.dll"
$output = Join-Path $PSScriptRoot "$name.daxstrategy"
if (-not (Test-Path $engine)) { throw "Canonical engine output not found: $engine" }
if (-not (Get-Command daxalgo-bundle -ErrorAction SilentlyContinue)) {
    throw "daxalgo-bundle is required. Install DaxAlgo.Strategy.BundleTool, then run this script again."
}

$publisherId = [string]$manifest.publisherId
if ([string]::IsNullOrWhiteSpace($publisherId)) {
    throw "plugin.json.publisherId is required and must be a stable lowercase marketplace identifier."
}
$arguments = @(
    'pack',
    '--id', [string]$manifest.id,
    '--name', [string]$manifest.name,
    '--version', [string]$manifest.version,
    '--publisher', $publisherId,
    '--sdk', [string]$manifest.targetSdkVersion,
    '--engine', $engine,
    '--entry-type', "$name.Engine.${name}Factory",
    '--output', $output
)

# Include every engine-private managed dependency copied by MSBuild. Host-owned contracts are shared by
# the loader and must never be bundled. The bundle verifier rejects native/mixed/UI dependencies.
$privateDependencies = @(Get-ChildItem $engineBin -File -Filter '*.dll' | Where-Object {
    $_.FullName -ne $engine -and
    $_.Name -ne "$name.Engine.dll" -and
    $_.Name -notmatch '^(TradingTerminal\.|DaxAlgo\.Sdk(?:\.|$))'
} | Sort-Object Name)
foreach ($dependency in $privateDependencies) {
    $arguments += @('--dependency', $dependency.FullName)
}
foreach ($capability in @($manifest.capabilities)) {
    if (-not [string]::IsNullOrWhiteSpace([string]$capability)) {
        $arguments += @('--capability', [string]$capability)
    }
}

# The legacy adapter exists in both variants. Only a scaffold that actually has WPF view files places it
# in the bundle's Windows UI role.
if (Test-Path (Join-Path $PSScriptRoot "$name\${name}Window.xaml")) {
    $arguments += @('--ui', $windows)
}

& daxalgo-bundle @arguments
if ($LASTEXITCODE -ne 0) { throw "Bundle packaging failed." }
Write-Host "Packaged -> $output"
