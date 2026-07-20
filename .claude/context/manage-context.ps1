param(
    [ValidateSet('summary', 'check', 'deep-check')]
    [string]$Action = 'summary'
)

# Public-only context manager. It is intentionally independent of any consuming overlay.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$layers = @(
    [pscustomobject]@{
        Name = 'windows'
        Deps = '.claude/context/deps.json'
        Index = '.claude/context/index'
        Symbols = '.claude/context/symbols'
        Generator = '.claude/context/gen-context.sh'
        Roots = @('src/windows', 'tests/TradingTerminal.Tests', 'tests/TradingTerminal.Tests.Headless', 'samples')
    },
    [pscustomobject]@{
        Name = 'linux'
        Deps = '.claude/context/linux/deps.json'
        Index = '.claude/context/linux/index'
        Symbols = '.claude/context/linux/symbols'
        Generator = '.claude/context/gen-context-linux.sh'
        Roots = @('src/linux', 'tests/linux')
    }
)

function Get-RelativeSourcePath([string]$FullName) {
    return $FullName.Substring($repoRoot.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-SourcePaths([string[]]$Roots) {
    $paths = foreach ($root in $Roots) {
        $full = Join-Path $repoRoot $root
        if (-not (Test-Path -LiteralPath $full)) { continue }
        Get-ChildItem -LiteralPath $full -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Extension.ToLowerInvariant() -in @('.cs', '.xaml', '.axaml') -and
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
            } |
            ForEach-Object { Get-RelativeSourcePath $_.FullName }
    }
    return @($paths | Sort-Object -Unique)
}

function Get-IndexedPaths([string]$Directory) {
    $paths = foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.md' -File -ErrorAction SilentlyContinue) {
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ($line -match '^\|\s+\x60([^\x60]+)\x60\s+\|') { $Matches[1].Replace('\', '/') }
        }
    }
    return @($paths)
}

function Test-Layer([object]$Layer, [Collections.Generic.List[string]]$Errors) {
    $depsPath = Join-Path $repoRoot $Layer.Deps
    $indexPath = Join-Path $repoRoot $Layer.Index
    $symbolsPath = Join-Path $repoRoot $Layer.Symbols
    foreach ($path in @($depsPath, $indexPath, $symbolsPath)) {
        if (-not (Test-Path -LiteralPath $path)) { $Errors.Add("$($Layer.Name): missing $path") }
    }
    if ($Errors.Count -gt 0 -and -not (Test-Path -LiteralPath $depsPath)) { return }

    try { $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json }
    catch { $Errors.Add("$($Layer.Name): invalid deps JSON: $($_.Exception.Message)"); return }
    $modules = @($deps.modules)
    $moduleNames = @($modules | ForEach-Object { [string]$_.module })
    $duplicates = @($moduleNames | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($duplicates) { $Errors.Add("$($Layer.Name): duplicate dependency modules: $($duplicates -join ', ')") }

    $projectNames = foreach ($root in $Layer.Roots) {
        $full = Join-Path $repoRoot $root
        if (Test-Path -LiteralPath $full) {
            Get-ChildItem -LiteralPath $full -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
                ForEach-Object BaseName
        }
    }
    $projectNames = @($projectNames | Sort-Object -Unique)
    $projectDiff = @(Compare-Object $projectNames ($moduleNames | Sort-Object -Unique))
    if ($projectDiff) {
        $Errors.Add("$($Layer.Name): deps project set differs from current csproj set")
    }

    $byName = @{}
    foreach ($module in $modules) { $byName[[string]$module.module] = $module }
    foreach ($module in $modules) {
        $name = [string]$module.module
        foreach ($dependency in @($module.dependsOn)) {
            $dependencyName = [string]$dependency
            if (-not $byName.ContainsKey($dependencyName)) {
                $Errors.Add("$($Layer.Name): $name depends on missing module $dependencyName")
                continue
            }
            $reverse = @($byName[$dependencyName].dependedBy | ForEach-Object { ([string]$_) -replace '^test:', '' })
            if ($name -notin $reverse) {
                $Errors.Add("$($Layer.Name): reverse edge missing for $name -> $dependencyName")
            }
        }
        foreach ($dependent in @($module.dependedBy)) {
            $dependentName = ([string]$dependent) -replace '^test:', ''
            if (-not $byName.ContainsKey($dependentName)) {
                $Errors.Add("$($Layer.Name): $name has missing dependent $dependentName")
                continue
            }
            if ($name -notin @($byName[$dependentName].dependsOn)) {
                $Errors.Add("$($Layer.Name): forward edge missing for $dependentName -> $name")
            }
        }
    }

    if (Test-Path -LiteralPath $indexPath) {
        $actual = Get-SourcePaths $Layer.Roots
        $indexed = Get-IndexedPaths $indexPath
        $indexDuplicates = @($indexed | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
        if ($indexDuplicates) { $Errors.Add("$($Layer.Name): duplicate index rows: $($indexDuplicates.Count)") }
        $indexDiff = @(Compare-Object $actual ($indexed | Sort-Object -Unique))
        if ($indexDiff) { $Errors.Add("$($Layer.Name): generated index path set is stale ($($indexDiff.Count) differences)") }
    }

    if (Test-Path -LiteralPath $symbolsPath) {
        foreach ($file in Get-ChildItem -LiteralPath $symbolsPath -Filter '*.md' -File) {
            foreach ($line in Get-Content -LiteralPath $file.FullName) {
                if ($line -match '^##\s+(.+[.](?:cs|xaml|axaml))\s*$') {
                    $target = Join-Path $repoRoot $Matches[1]
                    if (-not (Test-Path -LiteralPath $target)) {
                        $Errors.Add("$($Layer.Name): missing symbol anchor $($Matches[1])")
                    }
                }
            }
        }
    }
}

function Test-ManagementGuidance([Collections.Generic.List[string]]$Errors) {
    $files = New-Object Collections.Generic.List[IO.FileInfo]
    foreach ($relative in @('AGENTS.md', '.claude/context/PROTOCOL.md')) {
        $path = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $path) { $files.Add((Get-Item -LiteralPath $path)) }
    }
    foreach ($relative in @('.claude/context/RECIPES', '.claude/context/modules', '.codex/agents', '.agents/skills')) {
        $path = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $path) {
            Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -in @('.md', '.toml') } |
                ForEach-Object { $files.Add($_) }
        }
    }

    foreach ($file in $files) {
        $relative = Get-RelativeSourcePath $file.FullName
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text -match 'src/TradingTerminal[./]') {
            $Errors.Add("management guidance uses the removed flat source layout: $relative")
        }
        if ($text -match 'shell-fix-triple[.]md') {
            $Errors.Add("management guidance links the retired shell recipe: $relative")
        }
        if ($relative -like '.codex/agents/*' -and
            $text -match 'TradingTerminal[.](?:Strategies[.]|Ai[.](?:MarketAnalyst|FactorResearch|MlFeatures|BacktestAnalysis|PaperLab)|Backtest[.]Cli)') {
            $Errors.Add("custom agent targets a removed Windows project: $relative")
        }
        foreach ($match in [regex]::Matches($text, '\x60((?:RECIPES|modules)/[^\x60]+[.]md)\x60')) {
            $target = Join-Path $PSScriptRoot $match.Groups[1].Value
            if (-not (Test-Path -LiteralPath $target)) {
                $Errors.Add(('broken context link in {0}: {1}' -f $relative, $match.Groups[1].Value))
            }
        }
    }
}

function Invoke-Check {
    $errors = New-Object Collections.Generic.List[string]
    foreach ($required in @(
        '.claude/context/index.md',
        '.claude/context/symbols.md',
        '.claude/context/deps.json',
        '.claude/context/linux/index.md',
        '.claude/context/linux/symbols.md',
        '.claude/context/linux/deps.json',
        '.claude/context/PROTOCOL.md',
        'tasks/README.md'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $required))) {
            $errors.Add("missing required context file: $required")
        }
    }
    foreach ($layer in $layers) { Test-Layer $layer $errors }
    Test-ManagementGuidance $errors
    if ($errors.Count) {
        $errors | ForEach-Object { [Console]::Error.WriteLine("context check: ERROR: $_") }
        return 1
    }
    [Console]::WriteLine('context check: PASS (project graphs, index coverage, and symbol anchors)')
    return 0
}

function Find-Bash {
    if ($env:OS -eq 'Windows_NT') {
        $gitBash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
        if (Test-Path -LiteralPath $gitBash) { return $gitBash }
    }
    $command = Get-Command bash -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    throw 'bash was not found; install Git Bash or run generator checks in a Bash environment.'
}

function Invoke-DeepCheck {
    $result = Invoke-Check
    if ($result -ne 0) { return $result }
    $bash = Find-Bash
    foreach ($layer in $layers) {
        $output = @(& $bash $layer.Generator '--check' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            [Console]::Error.WriteLine("$($Layer.Name) generator check failed:")
            $output | Select-Object -Last 40 | ForEach-Object { [Console]::Error.WriteLine([string]$_) }
            return 1
        }
        [Console]::WriteLine("$($layer.Name) generator check: PASS")
    }
    return 0
}

Set-Location -LiteralPath $repoRoot
switch ($Action) {
    'summary' {
        $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
        $dirty = @(& git status --porcelain --untracked-files=all 2>$null).Count
        $parts = foreach ($layer in $layers) {
            $deps = Get-Content -LiteralPath (Join-Path $repoRoot $layer.Deps) -Raw | ConvertFrom-Json
            "$($layer.Name)=$(@($deps.modules).Count)p"
        }
        Write-Output "context summary: branch=$branch | dirty=$dirty | $($parts -join ' | ')"
        Write-Output 'run: powershell -File .claude/context/manage-context.ps1 check'
    }
    'check' { exit (Invoke-Check) }
    'deep-check' { exit (Invoke-DeepCheck) }
}
