# Stop hook: structural-drift reminder. Fires ONCE when project structure changed
# (.sln or .csproj added / removed / renamed) but no docs / CLAUDE.md / .claude updates are
# staged. Routine .cs/.xaml edits do NOT trigger it -- only structural changes, so low friction.
# Honors stop_hook_active so it never loops.

$ErrorActionPreference = 'Continue'

$inputJson = [Console]::In.ReadToEnd()
$hookInput = $null
if ($inputJson) { try { $hookInput = $inputJson | ConvertFrom-Json } catch { } }
if ($hookInput -and $hookInput.stop_hook_active) { exit 0 }

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

$status = & git status --porcelain 2>$null
if (-not $status) { exit 0 }

# Structural change = a .sln/.csproj line marked added / deleted / renamed / untracked.
$structural = $status | Where-Object { $_ -match '^\s*(A|D|R|\?\?).*\.(sln|csproj)\b' }
if (-not $structural) { exit 0 }

# If docs / CLAUDE.md / .claude were also touched, assume the author kept them in sync.
$docsTouched = $status | Where-Object { $_ -match '(CLAUDE\.md|docs/|\.claude/)' }
if ($docsTouched) { exit 0 }

$reason = "Project structure changed (.sln/.csproj added/removed/renamed) but no docs/CLAUDE.md/.claude " +
          "updates are staged. Sync the affected docs (architecture.md project graph, README, strategies.md), " +
          "the CLAUDE.md project map, and any skill paths (navigator/market-data-pipeline/ai-analyst/add-*). " +
          "If this is intentional or already covered, just stop again -- this fires only once."

$payload = [ordered]@{
    decision = "block"
    reason   = $reason
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
