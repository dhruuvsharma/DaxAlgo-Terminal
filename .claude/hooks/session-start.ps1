# SessionStart hook: injects a cheap one-time orientation (branch, last commit, dirty count)
# into the session as additionalContext. No build -- fast. Helps recall on a fresh session.

$ErrorActionPreference = 'SilentlyContinue'

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

$branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
$last   = (& git log -1 --pretty=format:'%h %s' 2>$null)
$dirty  = ((& git status --porcelain --untracked-files=all 2>$null) | Measure-Object -Line).Lines

$ctx = "DaxAlgo Terminal orientation - branch: $branch | last commit: $last | uncommitted files: $dirty. " +
       "Follow AGENTS.md; load .claude/context/index.md + symbols.md + deps.json + PROTOCOL.md before Windows source. Linux uses .claude/context/linux/. " +
       "INDEPENDENT TREES: Windows/WPF = src/windows + TradingTerminal.Windows.slnx; Linux/Avalonia = src/linux + TradingTerminal.Linux.slnx. " +
       "Use a Windows edition .slnf by default; run App.Basic or App.Intermediate explicitly. " +
       "Data/signals only (no live order execution); one universal Activity Log."

$payload = [ordered]@{
    hookSpecificOutput = [ordered]@{
        hookEventName     = 'SessionStart'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
