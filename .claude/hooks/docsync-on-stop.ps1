# Stop hook: structural-drift reminder for the public repository. It notices project/solution
# edits and added/deleted/renamed source or resource paths; ordinary implementation edits are quiet.

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
$structural = @($status | Where-Object {
    if ($_.Length -lt 4) { return $false }
    $code = $_.Substring(0,2)
    $path = $_.Substring(3)
    $configChanged = $path -match '[.](csproj|slnx|slnf|props|targets)$'
    $pathSetChanged = $code -match '[ADR?]' -and
        $path -match '[.](cs|xaml|axaml|html|js|ts|tsx|css|json|xshd|svg|py|cpp|h|hpp|c|cu|cuh|sh|ps1)$'
    $configChanged -or $pathSetChanged
})
if (-not $structural) { exit 0 }

$manager = Join-Path $projectDir '.claude/context/manage-context.ps1'
if (Test-Path -LiteralPath $manager) {
    $powerShellExe = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile')
    if ($env:OS -eq 'Windows_NT') { $arguments += @('-ExecutionPolicy', 'Bypass') }
    $arguments += @('-File', $manager, 'check')
    $output = @(& $powerShellExe @arguments 2>&1)
    if ($LASTEXITCODE -eq 0) { exit 0 }
    $details = ($output | Select-Object -Last 12 | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
} else {
    $details = 'public context manager is missing'
}

$reason = "Project/source structure changed and the public context check is stale or unavailable. " +
          "Regenerate the affected Windows or Linux slice and update the dependency masters." +
          [Environment]::NewLine + $details

[ordered]@{ decision='block'; reason=$reason } | ConvertTo-Json -Compress
exit 0
