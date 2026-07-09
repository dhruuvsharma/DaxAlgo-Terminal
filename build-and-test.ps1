<#
  Windows build + test for DaxAlgo Terminal (Windows/WPF tree).
  The repo is split into two fully independent trees with NO shared code:
    - Windows/WPF  : src/windows  + TradingTerminal.Windows.slnx
    - Linux/Avalonia: src/linux   + TradingTerminal.Linux.slnx  (see linux/build-and-test.sh)
  Run from anywhere:  powershell -File .\build-and-test.ps1   (or pwsh on PS7)
#>
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

Write-Host '### BUILD - Windows solution (WPF, net9.0-windows7.0)' -ForegroundColor Cyan
dotnet build TradingTerminal.Windows.slnx -clp:NoSummary -v q

Write-Host '### TEST - headless suite' -ForegroundColor Cyan
dotnet test tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --nologo -v q

Write-Host '### TEST - Windows-only suite (WPF / DuckDB / AI)' -ForegroundColor Cyan
dotnet test tests/TradingTerminal.Tests/TradingTerminal.Tests.csproj --nologo -v q

Write-Host ''
Write-Host 'Run the WPF app (Intermediate):        dotnet run --project src/windows/Shell/TradingTerminal.App.Intermediate' -ForegroundColor Green
Write-Host 'Run the WPF app (Basic):               dotnet run --project src/windows/Shell/TradingTerminal.App.Basic' -ForegroundColor Green
Write-Host 'Professional edition + backtest CLI:   private DaxAlgo-Terminal-Pro repo (TradingTerminal.Pro.slnx)' -ForegroundColor Green
Write-Host 'Linux/Avalonia tree builds separately: see linux/build-and-test.sh' -ForegroundColor Green
