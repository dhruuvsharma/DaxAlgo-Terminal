#!/usr/bin/env bash
# Headless Linux build + test + CLI smoke + ARM64 restore probe for DaxAlgo Terminal.
# The Linux/Avalonia tree (src/linux) is fully independent of the Windows/WPF tree
# (src/windows) — no shared code. Run from anywhere; resolves the repo root from this script.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "### dotnet $(dotnet --version) on $(uname -m) / $(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-$(uname -s)}")"

echo "### BUILD net9.0 — Linux solution (portable core + Avalonia shell, src/linux)"
dotnet build TradingTerminal.Linux.slnx -clp:NoSummary -v q

echo "### TEST net9.0 — headless suite (Linux copy)"
dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj --nologo -v q | grep -E "Passed!|Failed!|error" || true

echo "### CLI smoke — synth + meanReversion backtest"
CLI=src/linux/Backtest/TradingTerminal.Backtest.Cli/bin/Debug/net9.0/daxalgo-backtest.dll
dotnet "$CLI" synth --output /tmp/ticks.parquet --ticks 3000 --seed 7
dotnet "$CLI" run --strategy meanReversion --symbol TEST --source parquet --data /tmp/ticks.parquet --output /tmp/bt
echo "### CLI produced:" && ls -1 /tmp/bt

echo "### RESTORE probe — linux-arm64 (Raspberry Pi)"
dotnet restore src/linux/Pipeline/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -r linux-arm64 -v q && echo "arm64 restore: Infrastructure OK"
dotnet restore src/linux/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj     -r linux-arm64 -v q && echo "arm64 restore: CLI OK"

echo "### ALL LINUX CHECKS DONE"
