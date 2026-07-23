param(
    [ValidateSet('summary', 'check', 'deep-check', 'gate-check', 'sync')]
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
                $_.Extension.ToLowerInvariant() -in @('.cs', '.xaml') -and
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
            } |
            ForEach-Object { Get-RelativeSourcePath $_.FullName }
    }
    return @($paths | Sort-Object -Unique)
}

function Get-PathHash([string[]]$Values) {
    $joined = (($Values | Sort-Object -Unique) -join "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($joined)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Get-ContextStateFiles {
    $files = New-Object System.Collections.Generic.List[IO.FileInfo]
    foreach ($layer in $layers) {
        foreach ($root in $layer.Roots) {
            $full = Join-Path $repoRoot $root
            if (-not (Test-Path -LiteralPath $full)) { continue }
            Get-ChildItem -LiteralPath $full -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Extension.ToLowerInvariant() -in @('.cs', '.xaml', '.csproj', '.props', '.targets') -and
                    $_.FullName -notmatch '[\\/](bin|obj|[.]git)[\\/]'
                } | ForEach-Object { $files.Add($_) }
        }
    }
    Get-ChildItem -LiteralPath $repoRoot -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension.ToLowerInvariant() -in @('.slnx', '.slnf', '.props', '.targets') } |
        ForEach-Object { $files.Add($_) }
    foreach ($relative in @('.claude/context', '.codex/agents', '.agents/skills')) {
        $full = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $full) {
            Get-ChildItem -LiteralPath $full -Recurse -File -ErrorAction SilentlyContinue |
                ForEach-Object { $files.Add($_) }
        }
    }
    foreach ($relative in @('AGENTS.md', 'tasks/README.md')) {
        $full = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $full) { $files.Add((Get-Item -LiteralPath $full)) }
    }
    return @($files | Sort-Object FullName -Unique)
}

function Get-ContextStateHash {
    $records = New-Object System.Collections.Generic.List[string]
    foreach ($file in Get-ContextStateFiles) {
        $relative = Get-RelativeSourcePath $file.FullName
        try {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
            $records.Add("$relative|$hash")
        } catch {
            $records.Add("$relative|<changed-during-read>")
        }
    }
    return Get-PathHash @($records)
}

function Get-ContextControlPath([string]$Name) {
    $gitDirectory = [string](& git -C $repoRoot rev-parse --git-dir 2>$null)
    if (-not $gitDirectory) { throw 'Cannot resolve the Git directory for context coordination.' }
    if (-not [IO.Path]::IsPathRooted($gitDirectory)) { $gitDirectory = Join-Path $repoRoot $gitDirectory }
    return Join-Path ([IO.Path]::GetFullPath($gitDirectory)) $Name
}

function Get-VerificationStamp {
    $path = Get-ContextControlPath 'codex-context-gate-v1.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { return $null }
}

function Write-VerificationStamp([string]$StateHash) {
    $path = Get-ContextControlPath 'codex-context-gate-v1.json'
    $temporaryPath = "$path.next.$PID.$([Guid]::NewGuid().ToString('N'))"
    $backupPath = "$path.backup.$PID.$([Guid]::NewGuid().ToString('N'))"
    $payload = [ordered]@{
        schemaVersion = 1
        stateHash = $StateHash
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json
    try {
        [IO.File]::WriteAllText($temporaryPath, $payload + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporaryPath, $path, $backupPath) }
        else { [IO.File]::Move($temporaryPath, $path) }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
        if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Force }
    }
}

function Invoke-WithContextLock([scriptblock]$Operation, [int]$TimeoutSeconds = 240) {
    $identity = (Get-PathHash @($repoRoot.ToLowerInvariant())).Substring(0, 24)
    $mutex = New-Object Threading.Mutex($false, "DaxAlgoContext_$identity")
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds)) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) {
            [Console]::Error.WriteLine('context operation: BUSY (another terminal still owns the repository context lock)')
            return 2
        }
        return (& $Operation)
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
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

    $projectFiles = foreach ($root in $Layer.Roots) {
        $full = Join-Path $repoRoot $root
        if (Test-Path -LiteralPath $full) {
            Get-ChildItem -LiteralPath $full -Recurse -Filter '*.csproj' -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
    }
    $projectFiles = @($projectFiles | Sort-Object FullName -Unique)
    $projectNames = @($projectFiles | ForEach-Object BaseName | Sort-Object -Unique)
    $projectDiff = @(Compare-Object $projectNames ($moduleNames | Sort-Object -Unique))
    if ($projectDiff) {
        $Errors.Add("$($Layer.Name): deps project set differs from current csproj set")
    }

    $byName = @{}
    foreach ($module in $modules) { $byName[[string]$module.module] = $module }
    foreach ($project in $projectFiles) {
        try {
            [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw
            $actualReferences = @($projectXml.SelectNodes('//ProjectReference') | ForEach-Object {
                if ($_.Include) { [IO.Path]::GetFileNameWithoutExtension([string]$_.Include) }
            } | Where-Object { $_ } | Sort-Object -Unique)
            $declaredReferences = @($byName[$project.BaseName].dependsOn | Sort-Object -Unique)
            if (($actualReferences -join ',') -ne ($declaredReferences -join ',')) {
                $Errors.Add("$($Layer.Name): dependency drift for $($project.BaseName)")
            }
        } catch {
            $Errors.Add("$($Layer.Name): cannot parse $($project.Name): $($_.Exception.Message)")
        }
    }
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
                if ($line -match '^##\s+(.+[.](?:cs|xaml))\s*$') {
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

function Invoke-Check([switch]$Quiet) {
    $script:lastContextCheckMessages = @()
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $beforeHash = Get-ContextStateHash
        try {
        $errors = New-Object Collections.Generic.List[string]
        foreach ($required in @(
            '.claude/context/index.md',
            '.claude/context/symbols.md',
            '.claude/context/deps.json',
            '.claude/context/PROTOCOL.md',
            'tasks/README.md'
        )) {
            if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $required))) {
                $errors.Add("missing required context file: $required")
            }
        }
        foreach ($layer in $layers) { Test-Layer $layer $errors }
        Test-ManagementGuidance $errors
        $afterHash = Get-ContextStateHash

        if ($beforeHash -ne $afterHash) {
            if ($attempt -lt 2) { Start-Sleep -Milliseconds 200; continue }
            $script:lastContextCheckMessages = @('context check: UNSTABLE (routed files changed while the snapshot was being validated)')
            if (-not $Quiet) { $script:lastContextCheckMessages | ForEach-Object { [Console]::WriteLine($_) } }
            return 2
        }
        if ($errors.Count) {
            $script:lastContextCheckMessages = @($errors | ForEach-Object { "context check: ERROR: $_" })
            if (-not $Quiet) { $script:lastContextCheckMessages | ForEach-Object { [Console]::Error.WriteLine($_) } }
            return 1
        }
        $script:lastContextCheckMessages = @('context check: PASS (project graphs, index coverage, and symbol anchors)')
        if (-not $Quiet) { [Console]::WriteLine($script:lastContextCheckMessages[0]) }
        return 0
        } catch {
            $caught = $_
            $afterHash = Get-ContextStateHash
            if ($beforeHash -ne $afterHash) {
                if ($attempt -lt 2) { Start-Sleep -Milliseconds 200; continue }
                $script:lastContextCheckMessages = @('context check: UNSTABLE (a routed file changed during validation)')
                if (-not $Quiet) { $script:lastContextCheckMessages | ForEach-Object { [Console]::WriteLine($_) } }
                return 2
            }
            $script:lastContextCheckMessages = @("context check: ERROR: stable validation failure: $($caught.Exception.Message)")
            if (-not $Quiet) { $script:lastContextCheckMessages | ForEach-Object { [Console]::Error.WriteLine($_) } }
            return 1
        }
    }
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

function Invoke-ContextGenerator([string]$Bash, [string]$Script, [string[]]$Arguments) {
    $previous = $env:DAXALGO_CONTEXT_LOCK_HELD
    $env:DAXALGO_CONTEXT_LOCK_HELD = '1'
    try {
        $output = @(& $Bash $Script @Arguments 2>&1)
        return [pscustomobject]@{ ExitCode=$LASTEXITCODE; Output=$output }
    } finally {
        if ($null -eq $previous) { Remove-Item Env:DAXALGO_CONTEXT_LOCK_HELD -ErrorAction SilentlyContinue }
        else { $env:DAXALGO_CONTEXT_LOCK_HELD = $previous }
    }
}

function Invoke-DeepCheck([switch]$UseStamp) {
    if ($UseStamp) {
        $stateHash = Get-ContextStateHash
        $stamp = Get-VerificationStamp
        if ($stamp -and $stamp.schemaVersion -eq 1 -and $stamp.stateHash -eq $stateHash) {
            $confirmedHash = Get-ContextStateHash
            if ($confirmedHash -eq $stateHash) {
                [Console]::WriteLine('context gate: PASS (reused the exact verified workspace fingerprint)')
                return 0
            }
        }
        $fastResult = Invoke-Check -Quiet
        if ($fastResult -ne 0) {
            $script:lastContextCheckMessages | ForEach-Object { [Console]::WriteLine($_) }
            return $fastResult
        }
        $stateHash = Get-ContextStateHash
        $stamp = Get-VerificationStamp
        if ($stamp -and $stamp.schemaVersion -eq 1 -and $stamp.stateHash -eq $stateHash) {
            $confirmedHash = Get-ContextStateHash
            if ($confirmedHash -eq $stateHash) {
                [Console]::WriteLine('context gate: PASS (reused the exact verified workspace fingerprint)')
                return 0
            }
        }
    }

    $bash = Find-Bash
    $maxAttempts = if ($UseStamp) { 1 } else { 2 }
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $beforeHash = Get-ContextStateHash
        $checkResult = Invoke-Check -Quiet
        $generatorOutput = @()
        $generatorExitCode = 0
        if ($checkResult -eq 0) {
            foreach ($layer in $layers) {
                $run = Invoke-ContextGenerator $bash $layer.Generator @('--check')
                $output = @($run.Output)
                $exitCode = $run.ExitCode
                $generatorOutput += $output
                if ($exitCode -ne 0) { $generatorExitCode = $exitCode; break }
            }
        }
        $afterHash = Get-ContextStateHash

        if ($beforeHash -ne $afterHash -or $checkResult -eq 2) {
            if ($attempt -lt $maxAttempts) { Start-Sleep -Milliseconds 250; continue }
            [Console]::WriteLine('context deep-check: UNSTABLE (another terminal changed or is verifying routed files; no stale verdict was recorded)')
            return 2
        }
        if ($checkResult -ne 0) {
            $script:lastContextCheckMessages | ForEach-Object { [Console]::WriteLine($_) }
            return 1
        }
        if ($generatorExitCode -ne 0) {
            [Console]::Error.WriteLine('windows generator check failed:')
            $generatorOutput | Select-Object -Last 40 | ForEach-Object { [Console]::Error.WriteLine([string]$_) }
            return 1
        }

        try { Write-VerificationStamp $afterHash }
        catch { [Console]::Error.WriteLine("context verification cache skipped: $($_.Exception.Message)") }
        [Console]::WriteLine('context check: PASS (project graphs, index coverage, and symbol anchors)')
        [Console]::WriteLine('windows generator check: PASS')
        return 0
    }
}

function Invoke-Sync {
    $bash = Find-Bash
    foreach ($layer in $layers) {
        $run = Invoke-ContextGenerator $bash $layer.Generator @()
        $output = @($run.Output)
        if ($run.ExitCode -ne 0) {
            $output | Select-Object -Last 60 | ForEach-Object { [Console]::Error.WriteLine([string]$_) }
            return 1
        }
    }
    return Invoke-DeepCheck
}

Set-Location -LiteralPath $repoRoot
switch ($Action) {
    'summary' {
        exit (Invoke-WithContextLock {
            $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
            $dirty = @(& git status --porcelain --untracked-files=all 2>$null).Count
            $parts = foreach ($layer in $layers) {
                $deps = Get-Content -LiteralPath (Join-Path $repoRoot $layer.Deps) -Raw | ConvertFrom-Json
                "$($layer.Name)=$(@($deps.modules).Count)p"
            }
            [Console]::WriteLine("context summary: branch=$branch | dirty=$dirty | $($parts -join ' | ')")
            [Console]::WriteLine('run: powershell -File .claude/context/manage-context.ps1 check')
            return 0
        } 2)
    }
    'check' { exit (Invoke-WithContextLock { Invoke-Check } 2) }
    'deep-check' { exit (Invoke-WithContextLock { Invoke-DeepCheck }) }
    'gate-check' { exit (Invoke-WithContextLock { Invoke-DeepCheck -UseStamp } 8) }
    'sync' { exit (Invoke-WithContextLock { Invoke-Sync }) }
}
