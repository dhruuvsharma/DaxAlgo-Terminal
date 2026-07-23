# Stop hook: generator-freshness gate for the public Windows repository. Routed source/config
# changes require a byte-for-byte context generator check before the turn can finish.

$ErrorActionPreference = 'Continue'

$inputJson = [Console]::In.ReadToEnd()
$hookInput = $null
if ($inputJson) { try { $hookInput = $inputJson | ConvertFrom-Json } catch { } }
if ($hookInput -and $hookInput.stop_hook_active) { exit 0 }

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -LiteralPath $projectDir

$status = @(& git status --porcelain --untracked-files=all 2>$null)
if (-not $status) { exit 0 }
$contextRelevant = @($status | Where-Object {
    if ($_.Length -lt 4) { return $false }
    $code = $_.Substring(0,2)
    $path = $_.Substring(3)
    $configChanged = $path -match '[.](csproj|slnx|slnf|props|targets)$'
    $sourceChanged = $path -match '^(src/windows|tests|samples)/' -and
        $path -match '[.](cs|xaml)$'
    $pathSetChanged = $code -match '[ADR?]' -and
        $path -match '[.](cs|xaml|html|js|ts|tsx|css|json|xshd|svg|py|cpp|h|hpp|c|cu|cuh|sh|ps1)$'
    $contextEngineChanged = $path -match '^\.claude/context/(gen-context[.]sh|manage-context[.]ps1)$'
    $contextDataChanged = $path -match '^\.claude/context/' -and $path -match '[.](md|json|sh|ps1)$'
    $configChanged -or $sourceChanged -or $pathSetChanged -or $contextEngineChanged -or $contextDataChanged
})
if (-not $contextRelevant) { exit 0 }

$manager = Join-Path $projectDir '.claude/context/manage-context.ps1'
if (Test-Path -LiteralPath $manager) {
    $powerShellExe = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile')
    if ($env:OS -eq 'Windows_NT') { $arguments += @('-ExecutionPolicy', 'Bypass') }
    $arguments += @('-File', $manager, 'gate-check')
    $output = @(& $powerShellExe @arguments 2>&1)
    $checkExitCode = $LASTEXITCODE
    # Exit 2 is deliberately deferred; the exact-state stamp cannot be reused until a stable Stop.
    if ($checkExitCode -eq 0 -or $checkExitCode -eq 2) { exit 0 }
    $details = ($output | Select-Object -Last 12 | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
} else {
    $details = 'public context manager is missing'
}

$reason = "Windows source/configuration changed and the public generated context is stale or unavailable. " +
          "Regenerate the Windows context, update dependency masters if needed, and rerun deep-check." +
          [Environment]::NewLine + $details

[ordered]@{ decision='block'; reason=$reason } | ConvertTo-Json -Compress
exit 0
