#!/usr/bin/env bash
# Headless Linux build + test + CLI smoke + ARM64 restore probe for DaxAlgo Terminal.
# Run from anywhere; resolves the repo root relative to this script.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "### dotnet $(dotnet --version) on $(uname -m) / $(. /etc/os-release 2>/dev/null; echo "${PRETTY_NAME:-$(uname -s)}")"

echo "### BUILD net9.0 — headless layer (src/shared) + Avalonia shell (src/linux)"
dotnet build src/shared/Pipeline/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj   -f net9.0 -clp:NoSummary -v q
dotnet build src/shared/Backtest/TradingTerminal.Backtest.Engine/TradingTerminal.Backtest.Engine.csproj -f net9.0 -clp:NoSummary -v q
dotnet build src/shared/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj       -f net9.0 -clp:NoSummary -v q
dotnet build src/linux/Shell/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj        -clp:NoSummary -v q

echo "### TEST net9.0 — headless suite"
dotnet test tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -f net9.0 --nologo -v q | grep -E "Passed!|Failed!|error" || true

echo "### CLI smoke — synth + meanReversion backtest"
CLI=src/shared/Backtest/TradingTerminal.Backtest.Cli/bin/Debug/net9.0/daxalgo-backtest.dll
dotnet "$CLI" synth --output /tmp/ticks.parquet --ticks 3000 --seed 7
dotnet "$CLI" run --strategy meanReversion --symbol TEST --source parquet --data /tmp/ticks.parquet --output /tmp/bt
echo "### CLI produced:" && ls -1 /tmp/bt

echo "### RESTORE probe — linux-arm64 (Raspberry Pi)"
dotnet restore src/shared/Pipeline/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -r linux-arm64 -v q && echo "arm64 restore: Infrastructure OK"
dotnet restore src/shared/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj     -r linux-arm64 -v q && echo "arm64 restore: CLI OK"

echo "### ALL LINUX CHECKS DONE"
