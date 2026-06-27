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
Write-Host 'Run the WPF app (Windows shell):       dotnet run --project src/windows/Shell/TradingTerminal.App' -ForegroundColor Green
Write-Host 'Run a backtest (CLI):                  dotnet run --project src/windows/Backtest/TradingTerminal.Backtest.Cli -- --help' -ForegroundColor Green
Write-Host 'Linux/Avalonia tree builds separately: see linux/build-and-test.sh' -ForegroundColor Green
