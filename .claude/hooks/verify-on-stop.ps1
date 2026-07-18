# Stop hook: the verifier's ENFORCEMENT ARM. Deterministic, high-confidence checks that
# BLOCK turn-end when the solution graph is violated or a broker SDK leaks into a lower layer.
# The `verifier` AGENT does the semantic/plan-adherence review; this hook is the hard gate the
# agent can't be -- it always runs on Stop, can't be skipped, and blocks.
#
# Only fires when .cs/.csproj changed. Honors stop_hook_active so it never loops. Conservative
# by design: it blocks ONLY on things that are violations by construction (no false positives).
# 2026-07-11: rewritten -- the original probed pre-fork paths (src\<Proj>\) so BOTH checks had
# been silently dead since the 2026-06-27 two-tree fork. Projects are now located by glob under
# src/ (covers src/windows AND src/linux), and the lower-layer path regex matches the forked
# layout including the DaxAlgo.Sdk projects.
# 2026-07-18: Infrastructure allowlist gains DaxAlgo.Codegen -- the lean AI-codegen assembly split
# out of Infrastructure (42a2dc2, #26 phase 4). It depends only on Core, so the reference is
# downward; Codegen itself is now gate-guarded to Core-only.
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
$changed = @($status) | ForEach-Object { ($_ -replace '^...').Trim() }
$relevant = $changed | Where-Object { $_ -match '\.(cs|csproj)$' }
if (-not $relevant) { exit 0 }

$violations = New-Object System.Collections.Generic.List[string]

# ---- 1. Solution-graph: lower-layer projects may reference only what the graph allows --------
# Checked whenever any .csproj changed. Both trees carry identically-named projects (they never
# compile together), so one glob per project name covers windows + linux copies.
$csprojChanged = $relevant | Where-Object { $_ -match '\.csproj$' }
if ($csprojChanged) {
    $allowed = @{
        'TradingTerminal.Core'           = @()
        'TradingTerminal.MarketData'     = @('TradingTerminal.Core')
        'TradingTerminal.UI.Core'        = @('TradingTerminal.Core')
        'TradingTerminal.UI'             = @('TradingTerminal.Core','TradingTerminal.UI.Core')
        'TradingTerminal.Infrastructure' = @('TradingTerminal.Core','TradingTerminal.MarketData','DaxAlgo.Sdk','DaxAlgo.Codegen')
        'DaxAlgo.Sdk'                    = @('TradingTerminal.Core')
        'DaxAlgo.Codegen'                = @('TradingTerminal.Core')
        'DaxAlgo.Sdk.Wpf'                = @('DaxAlgo.Sdk','TradingTerminal.UI','TradingTerminal.UI.Core')
    }
    foreach ($proj in $allowed.Keys) {
        $csprojs = Get-ChildItem -Path (Join-Path $projectDir 'src') -Recurse -Filter "$proj.csproj" -File -ErrorAction SilentlyContinue
        foreach ($csproj in $csprojs) {
            $refs = Select-String -Path $csproj.FullName -Pattern 'ProjectReference\s+Include="[^"]*\\([^\\"]+)\.csproj"' -AllMatches |
                    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value }
            foreach ($ref in $refs) {
                if ($allowed[$proj] -notcontains $ref) {
                    $ok = $allowed[$proj] -join ', '
                    if (-not $ok) { $ok = 'nothing' }
                    $rel = $csproj.FullName.Replace($projectDir, '').TrimStart('\')
                    $violations.Add("$rel references $ref -- violates the solution graph (allowed: $ok).")
                }
            }
        }
    }
}

# ---- 2. Broker SDK leak: SDK namespaces must stay under Infrastructure/<Broker>/ -------------
# Scan only CHANGED .cs files that live in a lower layer -- fast + targeted.
$sdkPattern = '^\s*using\s+(IBApi|NinjaTrader|NTDirect|Alpaca\.Markets|OpenAPI|Spotware)\b'
$lowerCs = $relevant | Where-Object {
    $_ -match '\.cs$' -and
    $_ -match '^src/(windows|linux)/[^/]+/(TradingTerminal\.(Core|MarketData|UI|UI\.Core)|DaxAlgo\.Sdk(\.Wpf)?)/'
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
