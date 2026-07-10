# SessionStart hook: injects a cheap one-time orientation (branch, last commit, dirty count)
# into the session as additionalContext. No build -- fast. Helps recall on a fresh session.

$ErrorActionPreference = 'SilentlyContinue'

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

$branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
$last   = (& git log -1 --pretty=format:'%h %s' 2>$null)
$dirty  = ((& git status --porcelain 2>$null) | Measure-Object -Line).Lines

$ctx = "DaxAlgo Terminal orientation - branch: $branch | last commit: $last | uncommitted files: $dirty. " +
       "Load the 'navigator' skill for the codebase map (which project owns what). " +
       "TWO INDEPENDENT TREES (no shared code): Windows/WPF = src/windows + TradingTerminal.Windows.slnx; Linux/Avalonia = src/linux + TradingTerminal.Linux.slnx. " +
       "Build/test: dotnet build TradingTerminal.Windows.slnx / dotnet run --project src/windows/Shell/TradingTerminal.App.Intermediate. " +
       "Data/signals only (no live order execution); one universal Activity Log."

$payload = [ordered]@{
    hookSpecificOutput = [ordered]@{
        hookEventName     = 'SessionStart'
        additionalContext = $ctx
    }
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
