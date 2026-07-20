param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('SessionStart', 'Stop')]
    [string]$Event
)

# Codex adapter for the canonical repository hooks under .claude/hooks/.
# It translates Codex stdin/cwd into the legacy hook environment and maps legacy JSON output to
# Codex's current systemMessage / continue / stopReason fields.

$ErrorActionPreference = 'Stop'

function Get-RepositoryRoot {
    param([object]$HookInput)

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($HookInput -and $HookInput.cwd) { $candidates.Add([string]$HookInput.cwd) }
    $candidates.Add((Get-Location).Path)
    $candidates.Add((Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path)

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $root = (& git -C $candidate rev-parse --show-toplevel 2>$null)
        if ($LASTEXITCODE -eq 0 -and $root) { return ([string]$root).Trim() }
    }

    throw 'Codex hook adapter could not resolve the repository root.'
}

function Invoke-CanonicalHook {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$InputJson,
        [Parameter(Mandatory = $true)][string]$PowerShellExe
    )

    $arguments = @('-NoProfile')
    if ($env:OS -eq 'Windows_NT') {
        $arguments += @('-ExecutionPolicy', 'Bypass')
    }
    $arguments += @('-File', $Path)

    $output = @($InputJson | & $PowerShellExe @arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Canonical hook failed ($exitCode): $Path`n$($output -join "`n")"
    }

    if (-not $output) { return $null }
    $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
    try { return ($text | ConvertFrom-Json) }
    catch { throw "Canonical hook returned invalid JSON: $Path`n$text" }
}

$inputJson = [Console]::In.ReadToEnd()
$hookInput = $null
if ($inputJson) {
    try { $hookInput = $inputJson | ConvertFrom-Json }
    catch { throw 'Codex hook adapter received invalid JSON on stdin.' }
}
if (-not $inputJson) { $inputJson = '{}' }

$repoRoot = Get-RepositoryRoot -HookInput $hookInput
$powerShellExe = (Get-Process -Id $PID).Path
$previousProjectDir = $env:CLAUDE_PROJECT_DIR
$env:CLAUDE_PROJECT_DIR = $repoRoot

try {
    if ($Event -eq 'SessionStart') {
        $result = Invoke-CanonicalHook -Path (Join-Path $repoRoot '.claude/hooks/session-start.ps1') -InputJson $inputJson -PowerShellExe $powerShellExe

        $context = $null
        if ($result -and $result.hookSpecificOutput) {
            $context = $result.hookSpecificOutput.additionalContext
        }
        if ($context) {
            [ordered]@{ systemMessage = [string]$context } | ConvertTo-Json -Compress
        }
        exit 0
    }

    $stopHooks = @(
        'build-on-stop.ps1',
        'verify-on-stop.ps1',
        'leakcheck-on-stop.ps1',
        'docsync-on-stop.ps1'
    )
    $reasons = New-Object System.Collections.Generic.List[string]

    foreach ($hookName in $stopHooks) {
        $result = Invoke-CanonicalHook -Path (Join-Path $repoRoot ".claude/hooks/$hookName") -InputJson $inputJson -PowerShellExe $powerShellExe
        if ($result -and $result.decision -eq 'block' -and $result.reason) {
            $reasons.Add([string]$result.reason)
        }
    }

    if ($reasons.Count -gt 0) {
        $reason = $reasons -join "`n`n"
        [ordered]@{
            continue      = $false
            stopReason    = $reason
            systemMessage = $reason
        } | ConvertTo-Json -Compress
    }
}
finally {
    $env:CLAUDE_PROJECT_DIR = $previousProjectDir
}

exit 0
