# Stop hook: surfaces dotnet build errors back to Claude before turn ends.
# Skips entirely when no .cs/.xaml/.csproj/.props files are dirty in git.
# Honors the stop_hook_active flag to avoid infinite loops.

$ErrorActionPreference = 'Continue'

# Read JSON payload from stdin
$inputJson = [Console]::In.ReadToEnd()
$hookInput = $null
if ($inputJson) {
    try { $hookInput = $inputJson | ConvertFrom-Json } catch { }
}

# Don't loop forever if Claude can't fix the build
if ($hookInput -and $hookInput.stop_hook_active) { exit 0 }

# Resolve project root
$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

# Skip if not a git repo or no relevant changes
$gitChanged = & git status --porcelain 2>$null
if (-not $gitChanged) { exit 0 }
$relevant = ($gitChanged | Where-Object { $_ -match '\.(cs|xaml|csproj|props)\b' })
if (-not $relevant) { exit 0 }

# Build silently. dotnet writes errors to stdout, so we don't redirect stderr.
$buildOutput = & dotnet build --nologo -v quiet | Out-String
$buildExit = $LASTEXITCODE

if ($buildExit -eq 0) { exit 0 }

# Extract just the error lines so we don't dump 200 lines into Claude's context
$errorLines = $buildOutput -split "`r?`n" |
    Where-Object { $_ -match '\b(error|Error)\s' } |
    Select-Object -First 25

# File-lock errors (MSB3027/MSB3021) mean the user has the app running — not a code defect.
# Don't block turn-end on these.
$realErrors = $errorLines | Where-Object { $_ -notmatch '\bMSB30(21|26|27)\b' }
if (-not $realErrors) { exit 0 }

$summary = $realErrors -join "`n"

$payload = [ordered]@{
    decision = "block"
    reason   = "dotnet build failed (exit $buildExit). Fix before finishing this turn:`n`n$summary"
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
