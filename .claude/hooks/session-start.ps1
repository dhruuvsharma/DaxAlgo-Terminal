# SessionStart hook: injects Windows-only orientation and a fast structural context-health result.
# It never rewrites context; the Stop gate enforces byte-for-byte generator freshness.

$ErrorActionPreference = 'SilentlyContinue'

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

$branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
$last   = (& git log -1 --pretty=format:'%h %s' 2>$null)
$dirty  = ((& git status --porcelain --untracked-files=all 2>$null) | Measure-Object -Line).Lines

$contextHealth = 'UNAVAILABLE'
$manager = Join-Path $projectDir '.claude/context/manage-context.ps1'
if (Test-Path -LiteralPath $manager) {
    $powerShellExe = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile')
    if ($env:OS -eq 'Windows_NT') { $arguments += @('-ExecutionPolicy', 'Bypass') }
    $arguments += @('-File', $manager, 'check')
    & $powerShellExe @arguments *> $null
    if ($LASTEXITCODE -eq 0) { $contextHealth = 'PASS' }
    elseif ($LASTEXITCODE -eq 2) { $contextHealth = 'BUSY/CHANGING' }
    else { $contextHealth = 'STALE' }
}

$ctx = "DaxAlgo Terminal Windows orientation - branch: $branch | last commit: $last | uncommitted files: $dirty | structural context check: $contextHealth. " +
       "Follow AGENTS.md; load .claude/context/index.md + symbols.md + deps.json + PROTOCOL.md before source. " +
       "This workspace is Windows/WPF only (src/windows + TradingTerminal.Windows.slnx). " +
       "Use a Windows edition .slnf by default and preserve Basic/Intermediate composition. " +
       "The Stop context gate enforces byte-for-byte generated-context freshness after routed code changes. Data/signals only; no live order execution."

$payload = [ordered]@{
    hookSpecificOutput = [ordered]@{
        hookEventName     = 'SessionStart'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
