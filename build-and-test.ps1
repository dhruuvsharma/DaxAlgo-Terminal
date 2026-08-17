<#
  Windows build + test for DaxAlgo Terminal (the base layer, authored in this checkout).
  Run from anywhere:  powershell -File .\build-and-test.ps1   (or pwsh on PS7)
#>
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

Write-Host '### BUILD - Windows solution (WPF, net9.0-windows7.0)' -ForegroundColor Cyan
dotnet build TradingTerminal.Windows.slnx -clp:NoSummary -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# -m:1 is REQUIRED, not a tuning knob.
#
# `dotnet test <solution>` runs one test invocation per project, in parallel, as separate processes.
# Three suites here (Execution.Tests, ExecutionUi.Tests, Sandbox.Runtime.Tests) construct execution
# books, and acquiring a book's lease takes a machine-wide named mutex
# (Global\DaxAlgoTerminal.Execution.Account.<sha>) — the product's one-writer-per-broker-account
# guard. Run concurrently, those suites race a singleton and the loser fails with "Another
# same-machine writer owns the execution account lease". That produced a different handful of red
# tests on every full run while each suite passed on its own.
#
# A machine-wide singleton cannot be exercised by two concurrent processes; that is a property of the
# system under test, not a defect in the tests. Serialising costs roughly 8s here and makes the result
# deterministic. Do NOT drop this flag, and do NOT instead weaken the mutex — it is a money-path
# safety property. (VSTest's own MaxCpuCount runsettings knob does NOT help: the parallelism is
# MSBuild's, one invocation per project, so it has to be capped at the MSBuild level.)
Write-Host '### TEST - full Windows solution (serialised; see comment above)' -ForegroundColor Cyan
dotnet test TradingTerminal.Windows.slnx --no-build --nologo -v q -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'Run the WPF app (Basic):             dotnet run --project src/windows/Shell/TradingTerminal.App.Basic' -ForegroundColor Green
Write-Host 'Professional edition + backtest CLI: private DaxAlgo-Terminal-Pro repo (TradingTerminal.Pro.slnx)' -ForegroundColor Green
