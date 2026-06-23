<#
  Windows build + test for DaxAlgo Terminal (both UI versions).
  Builds the full solution (WPF + Avalonia + headless) and runs the full test suite.
  Run from anywhere:  powershell -File .\build-and-test.ps1   (or pwsh on PS7)
  Linux equivalent:   linux/build-and-test.sh
#>
$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

Write-Host '### BUILD - full solution (WPF + Avalonia + headless)' -ForegroundColor Cyan
dotnet build -clp:NoSummary -v q

Write-Host '### TEST - headless suite (also runs on Linux)' -ForegroundColor Cyan
dotnet test tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --nologo -v q

Write-Host '### TEST - Windows-only suite (WPF / DuckDB / AI)' -ForegroundColor Cyan
dotnet test tests/TradingTerminal.Tests/TradingTerminal.Tests.csproj --nologo -v q

Write-Host ''
Write-Host 'Run the WPF app (Windows shell):       dotnet run --project src/TradingTerminal.App' -ForegroundColor Green
Write-Host 'Run the Avalonia app (cross-platform): dotnet run --project src/linux/TradingTerminal.App.Avalonia' -ForegroundColor Green
Write-Host 'Run a backtest (CLI):                  dotnet run --project src/shared/TradingTerminal.Backtest.Cli -- --help' -ForegroundColor Green
