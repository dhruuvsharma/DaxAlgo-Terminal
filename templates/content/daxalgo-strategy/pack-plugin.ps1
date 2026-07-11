# Packages this strategy as a .daxplugin — the integrity-verified format DaxAlgo Terminal's
# Plugin Manager installs (Plugins -> Manage strategy plugins... -> Install plugin...).
# The package is a zip of the plugin files plus a package.json integrity index (per-file sha256 +
# the main assembly name); the host verifies every file BEFORE any trust gate sees the content.
# Works in Windows PowerShell 5.1 and pwsh.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"

$name = "DaxNewStrategy"
$project = Join-Path $PSScriptRoot "$name\$name.csproj"

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$bin = Join-Path $PSScriptRoot "$name\bin\$Configuration\net9.0-windows7.0"
$stage = Join-Path ([IO.Path]::GetTempPath()) ("daxplugin-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $stage | Out-Null
try {
    # The plugin package = main dll + manifest (+ pdb/deps.json). Add any plugin-PRIVATE dependency
    # dlls your csproj copies to bin here too — NEVER TradingTerminal.*/DaxAlgo.Sdk* (the host
    # provides those; a shipped copy breaks type identity).
    foreach ($file in @("$name.dll", "plugin.json", "$name.pdb", "$name.deps.json")) {
        $source = Join-Path $bin $file
        if (Test-Path $source) { Copy-Item $source $stage }
    }
    if (-not (Test-Path (Join-Path $stage "$name.dll"))) { throw "Build output not found under $bin." }

    # Integrity index — must match the host's reader: forward-slash keys, hex sha256.
    $files = @{}
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
    @{ formatVersion = 1; mainAssembly = "$name.dll"; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage "package.json") -Encoding utf8

    $output = Join-Path $PSScriptRoot "$name.daxplugin"
    Remove-Item $output -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $output)
    Write-Host "Packaged -> $output"
    Write-Host "Install it via DaxAlgo Terminal: Plugins -> Manage strategy plugins... -> Install plugin..."
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
