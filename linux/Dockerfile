# Reproducible headless (Linux) build + test for DaxAlgo Terminal.
# Build from the repo root:
#   docker build -f linux/Dockerfile -t daxalgo-linux .
# The image fails the build if the Linux tree fails to compile or any test fails.
# The Linux/Avalonia tree (src/linux) is fully independent of the Windows/WPF tree (src/windows).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .

# Linux solution — portable core (net9.0) + Avalonia shell. The Windows/WPF tree is never built here.
RUN dotnet build TradingTerminal.Linux.slnx -c Release

# Headless test suite (Linux copy).
RUN dotnet test tests/linux/TradingTerminal.Tests.Headless/TradingTerminal.Tests.Headless.csproj -c Release

# Raspberry Pi (ARM64) restore probe — proves the headless packages resolve for linux-arm64.
RUN dotnet restore src/linux/Pipeline/TradingTerminal.Infrastructure/TradingTerminal.Infrastructure.csproj -r linux-arm64 \
 && dotnet restore src/linux/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -r linux-arm64 \
 && echo "ARM64 restore OK"

# Publish the headless backtest CLI as the image entrypoint.
RUN dotnet publish src/linux/Backtest/TradingTerminal.Backtest.Cli/TradingTerminal.Backtest.Cli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "daxalgo-backtest.dll"]
