# Reproducible headless (Linux) build + test for DaxAlgo Terminal.
# Build from the repo root:
#   docker build -f linux/Dockerfile -t daxalgo-linux .
# The image fails the build if any headless project fails to compile or any test fails.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .

# Headless layer (net9.0 leg only — the WPF projects are never built here).
RUN dotnet build src/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -f net9.0 -c Release \
 && dotnet build src/TradingTerminal.Backtest.Engine/TradingTerminal.Backtest.Engine.csproj -f net9.0 -c Release \
 && dotnet build src/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -f net9.0 -c Release

# Cross-platform Avalonia shell (Phase 1) — proves the UI stack builds on Linux atop the portable core.
RUN dotnet build src/TradingTerminal.App.Avalonia/TradingTerminal.App.Avalonia.csproj -c Release

RUN dotnet test tests/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -f net9.0 -c Release

# Raspberry Pi (ARM64) restore probe — proves the headless packages resolve for linux-arm64.
RUN dotnet restore src/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -r linux-arm64 \
 && dotnet restore src/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -r linux-arm64 \
 && echo "ARM64 restore OK"

# Publish the headless backtest CLI as the image entrypoint.
RUN dotnet publish src/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -f net9.0 -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "daxalgo-backtest.dll"]
