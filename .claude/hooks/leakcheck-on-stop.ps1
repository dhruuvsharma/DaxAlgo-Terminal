# Stop hook: MEMORY-LEAK net. Scans changed .cs for the patterns that have actually blown up RAM in
# this app (the Volume Footprint window hit ~20 GB) and asks Claude to confirm each is handled before
# the turn ends. Fires once per turn (honors stop_hook_active). Net, not a substitute for the
# `memory-safety` skill -- which is what to load when it fires.
#
# Patterns (see .claude/skills/memory-safety/SKILL.md):
#   MUST FIX  unbounded channel feeding the UI            -> Channel.CreateBounded(... DropOldest)
#   REVIEW    timer created but never Stop()/Dispose()d
#   REVIEW    VM owns a timer/channel/CTS but isn't IDisposable (shell can't tear it down on close)
#   REVIEW    brush/pen/typeface allocated in a render/redraw path (hoist to cached static)
#
# Run manually over the whole tree:  ... -File .claude\hooks\leakcheck-on-stop.ps1 -All
# ASCII-only on purpose -- Windows PowerShell 5.1 reads no-BOM files as ANSI and mangles non-ASCII.

param([switch]$All)

$ErrorActionPreference = 'Continue'

if (-not $All) {
    $inputJson = [Console]::In.ReadToEnd()
    $hookInput = $null
    if ($inputJson) { try { $hookInput = $inputJson | ConvertFrom-Json } catch { } }
    if ($hookInput -and $hookInput.stop_hook_active) { exit 0 }
}

$projectDir = $env:CLAUDE_PROJECT_DIR
if (-not $projectDir) { $projectDir = (Get-Location).Path }
Set-Location -Path $projectDir

# ---- File set: whole src/ for -All, else just changed .cs under src/ -------------------------
$files = @()
if ($All) {
    $srcDir = Join-Path $projectDir 'src'
    if (Test-Path $srcDir) {
        $files = Get-ChildItem -Path $srcDir -Recurse -Filter *.cs -File |
                 Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
                 ForEach-Object { $_.FullName }
    }
} else {
    $status = & git status --porcelain --untracked-files=all 2>$null
    if (-not $status) { exit 0 }
    $changed = $status | ForEach-Object { ($_ -replace '^...').Trim() }
    $files = $changed |
        Where-Object { $_ -match '\.cs$' -and $_ -match '^src/' -and $_ -notmatch '/(obj|bin)/' } |
        ForEach-Object { Join-Path $projectDir ($_ -replace '/','\') }
}
# Scope to UI-facing code only -- the charts/tools/strategies/AI/ML windows + shared UI/App shell.
# The broker clients (Infrastructure) and the canonical pipeline (MarketData) use channels on the
# PRODUCER side with their own backpressure; those are covered by broker-gotchas / market-data-pipeline,
# not this WPF-memory net. Core/Login carry no streaming UI.
$files = $files | Where-Object { $_ -notmatch '\\TradingTerminal\.(Infrastructure|MarketData|Core|Login)\\' }
if (-not $files) { if ($All) { Write-Output 'leakcheck: no UI-facing .cs files to scan.' }; exit 0 }

$findings = New-Object System.Collections.Generic.List[object]
function Add-Finding($sev, $file, $line, $tag, $hint) {
    $findings.Add([pscustomobject]@{ Sev = $sev; File = $file; Line = $line; Tag = $tag; Hint = $hint })
}

foreach ($full in ($files | Sort-Object -Unique)) {
    if (-not (Test-Path $full)) { continue }
    $rel = $full.Replace($projectDir, '').TrimStart('\')
    $text = Get-Content -Path $full -Raw
    if (-not $text) { continue }

    # 1. MUST FIX -- unbounded channel feeding the UI (the canonical 20 GB leak).
    $m = [regex]::Matches($text, 'Channel\.CreateUnbounded')
    if ($m.Count -gt 0) {
        $ln = ($text.Substring(0, $m[0].Index) -split "`n").Count
        Add-Finding 'MUST FIX' $rel $ln 'UNBOUNDED-CHANNEL' 'Use Channel.CreateBounded(new BoundedChannelOptions(cap){ FullMode = BoundedChannelFullMode.DropOldest }).'
    }

    $ownsTimer = [regex]::IsMatch($text, 'new\s+DispatcherTimer|new\s+System\.Timers\.Timer|new\s+System\.Threading\.Timer')
    # A long-lived UI resource (timer or channel) is what pins a VM after close. A per-run
    # CancellationTokenSource is NOT counted here -- it is benign and far too common to be useful.
    $ownsRes   = $ownsTimer -or [regex]::IsMatch($text, 'Channel\.Create(Bounded|Unbounded)')
    $disposable = [regex]::IsMatch($text, 'IDisposable') -or [regex]::IsMatch($text, '\bvoid\s+Dispose\s*\(')
    $managedBase = [regex]::IsMatch($text, ':\s*[\w<>, ]*\b(LiveSignalStrategyViewModelBase|StrategyWindowBase|SingleInstrumentHeatmapViewModelBase)\b')

    # 2. REVIEW -- timer created but never stopped/disposed anywhere in the file.
    if ($ownsTimer -and -not [regex]::IsMatch($text, '\.Stop\(\)|\.Dispose\(\)')) {
        $tm = [regex]::Match($text, 'new\s+(DispatcherTimer|System\.Timers\.Timer|System\.Threading\.Timer)')
        $ln = ($text.Substring(0, $tm.Index) -split "`n").Count
        Add-Finding 'REVIEW' $rel $ln 'TIMER-NO-STOP' 'Timer is never Stop()/Dispose()d -- stop it in Dispose() so it does not pin the VM after close.'
    }

    # 3. REVIEW -- owns a timer/channel that is never torn down AND the class cannot be disposed
    # (and derives from no lifecycle-managing base). A resource that IS stopped/disposed somewhere is
    # exempt (e.g. a local self-stopping timer), so this targets the genuine "never released" case.
    if ($ownsRes -and -not $disposable -and -not $managedBase -and
        -not [regex]::IsMatch($text, '\.Stop\(\)|\.Dispose\(\)')) {
        Add-Finding 'REVIEW' $rel 0 'NOT-DISPOSABLE' 'Owns a timer/channel that is never stopped/disposed and the class is not IDisposable -- the shell tears a VM down on Close only if it implements IDisposable; otherwise RAM never drops after the window closes.'
    }

    # 4. REVIEW -- brush/pen/typeface allocated in a WPF custom-draw path (should be cached static).
    # Require a genuine immediate-mode draw signal (OnRender / DrawingContext / InvalidateVisual);
    # ScottPlot's .Refresh() is NOT custom draw, so it is deliberately excluded to cut false hits.
    if ([regex]::IsMatch($text, '\bOnRender\s*\(|DrawingContext|InvalidateVisual\s*\(')) {
        # FormattedText is deliberately excluded -- it is a necessary per-string allocation in WPF
        # text rendering; what matters is caching the Typeface/Brush it consumes (and those ARE flagged).
        $allLines = $text -split "`r?`n"
        foreach ($mm in [regex]::Matches($text, 'new\s+(SolidColorBrush|Pen|Typeface|FontFamily)\(')) {
            $ln = ($text.Substring(0, $mm.Index) -split "`n").Count
            # Look at the match's line plus the two above it -- multi-line field initializers are common.
            # Skip the established-correct patterns: a cached `static` field, or an allocation that is
            # immediately Frozen / stored in a brush Cache (a once-per-colour factory, not per-frame).
            $from = [Math]::Max(0, $ln - 3)
            $window = ($allLines[$from..($ln - 1)] -join " ")
            if ($window -match '\bstatic\b|Freeze|Frozen|Cache') { continue }
            Add-Finding 'REVIEW' $rel $ln 'RENDER-ALLOC' 'Allocating a brush/pen/typeface in a render path -- hoist to a cached static readonly field and Freeze() it.'
            break
        }
    }
}

# ---- Manual mode: print a human report, never block. ----------------------------------------
if ($All) {
    if ($findings.Count -eq 0) { Write-Output 'leakcheck: clean -- no known leak patterns found.'; exit 0 }
    Write-Output "leakcheck: $($findings.Count) item(s) to review (see the memory-safety skill):`n"
    foreach ($f in $findings) {
        $loc = if ($f.Line -gt 0) { "$($f.File):$($f.Line)" } else { $f.File }
        Write-Output ("[{0}] {1} {2}`n    {3}" -f $f.Sev, $f.Tag, $loc, $f.Hint)
    }
    exit 0
}

# ---- Hook mode: block once if anything was found. -------------------------------------------
if ($findings.Count -eq 0) { exit 0 }

$lines = $findings | ForEach-Object {
    $loc = if ($_.Line -gt 0) { "$($_.File):$($_.Line)" } else { $_.File }
    "- [$($_.Sev)] $($_.Tag) $loc -- $($_.Hint)"
}
$reason = "MEMORY-LEAK GATE -- changed code touches patterns that have ballooned RAM in this app. " +
          "Load the `memory-safety` skill, then for EACH item below either fix it or confirm it is already handled " +
          "(bounded channel, batch-drain, coalesced redraw, IDisposable teardown). If all are safe, stop again to proceed:`n`n" +
          ($lines -join "`n") +
          "`n`nMUST FIX items are leaks by construction. This gate fires once per turn."

$payload = [ordered]@{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
Write-Output $payload
exit 0
