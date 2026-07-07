# Stop hook: the verifier's ENFORCEMENT ARM. Deterministic, high-confidence checks that
# BLOCK turn-end when the solution graph is violated or a broker SDK leaks into a lower layer.
# The `verifier` AGENT does the semantic/plan-adherence review; this hook is the hard gate the
# agent can't be -- it always runs on Stop, can't be skipped, and blocks.
#
# Only fires when .cs/.csproj changed. Honors stop_hook_active so it never loops. Conservative
# by design: it blocks ONLY on things that are violations by construction (no false positives).
# ASCII-only on purpose -- Windows PowerShell 5.1 reads no-BOM files as ANSI and mangles non-ASCII.

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
$changed = $status | ForEach-Object { ($_ -replace '^...').Trim() }
$relevant = $changed | Where-Object { $_ -match '\.(cs|csproj)$' }
if (-not $relevant) { exit 0 }

$violations = New-Object System.Collections.Generic.List[string]

# ---- 1. Solution-graph: lower-layer projects may reference only what the graph allows --------
# Checked whenever any .csproj changed (reading four files is cheap).
$csprojChanged = $relevant | Where-Object { $_ -match '\.csproj$' }
if ($csprojChanged) {
    $allowed = @{
        'TradingTerminal.Core'           = @()
        'TradingTerminal.MarketData'     = @('TradingTerminal.Core')
        'TradingTerminal.UI'             = @('TradingTerminal.Core')
        'TradingTerminal.Infrastructure' = @('TradingTerminal.Core','TradingTerminal.MarketData')
    }
    foreach ($proj in $allowed.Keys) {
        $csproj = Join-Path $projectDir "src\$proj\$proj.csproj"
        if (-not (Test-Path $csproj)) { continue }
        $refs = Select-String -Path $csproj -Pattern 'ProjectReference\s+Include="[^"]*\\([^\\"]+)\.csproj"' -AllMatches |
                ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
        foreach ($ref in $refs) {
            if ($allowed[$proj] -notcontains $ref) {
                $ok = $allowed[$proj] -join ', '
                if (-not $ok) { $ok = 'nothing' }
                $violations.Add("$proj references $ref -- violates the solution graph (allowed: $ok).")
            }
        }
    }
}

# ---- 2. Broker SDK leak: SDK namespaces must stay under Infrastructure/<Broker>/ -------------
# Scan only CHANGED .cs files that live in a lower layer (Core/MarketData/UI) -- fast + targeted.
$sdkPattern = '^\s*using\s+(IBApi|NinjaTrader|NTDirect|Alpaca\.Markets|OpenAPI|Spotware)\b'
$lowerCs = $relevant | Where-Object {
    $_ -match '\.cs$' -and $_ -match '^src/TradingTerminal\.(Core|MarketData|UI)/'
}
foreach ($file in $lowerCs) {
    $full = Join-Path $projectDir ($file -replace '/','\')
    if (-not (Test-Path $full)) { continue }
    $hit = Select-String -Path $full -Pattern $sdkPattern -List
    if ($hit) {
        $sdk = $hit.Matches[0].Groups[1].Value
        $violations.Add("$file imports a broker SDK ($sdk) -- SDK types must stay under Infrastructure\<Broker>\, behind IBrokerClient.")
    }
}

if ($violations.Count -eq 0) { exit 0 }

$reason = "VERIFIER GATE -- layer-graph / SDK-leak violations (fix before finishing this turn):`n`n" +
          (($violations | ForEach-Object { "- $_" }) -join "`n") +
          "`n`nThese are violations by construction. Re-route the change so lower layers stay clean. " +
          "If you believe this is a false positive, stop again -- the gate fires only once per turn."

$payload = [ordered]@{
    decision = "block"
    reason   = $reason
} | ConvertTo-Json -Compress

Write-Output $payload
exit 0
