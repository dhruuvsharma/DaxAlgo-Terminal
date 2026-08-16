using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Infrastructure.Strategies.Authoring;

namespace TradingTerminal.Backtest;

/// <summary>
/// Hyperion Prove path: real <see cref="IBacktestSession"/> run (bar-synthetic by default), not the
/// lifecycle smoke. Same stats calculator as Quick Backtest so arb / fade / breakout all fill one strip.
/// </summary>
public sealed class AuthoringProveService(
    IBacktestStrategyRegistry registry,
    IBacktestSession session,
    IBrokerSelector brokers,
    ILogger<AuthoringProveService> logger) : IAuthoringProveService
{
    public bool CanRun => brokers.AvailableKinds.Any();

    public async Task<AuthoringProveResult> RunAsync(string strategyOptionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(strategyOptionId))
            return Fail("Give the strategy an id before proving it.");

        var option = registry.Find(strategyOptionId);
        if (option is null)
            return Fail($"'{strategyOptionId}' is not registered yet — Compile & Register first.");

        var broker = PickBroker();
        if (!brokers.IsAvailable(broker))
            return Fail("No market-data broker is available. Connect Binance or Simulated, then retry.");

        var client = brokers.Get(broker);
        var contract = PickContract(broker);
        var lookback = TimeSpan.FromDays(7);
        var barSize = BarSize.OneHour;
        var tickSize = broker == BrokerKind.Binance ? 0.01 : 0.01;
        var feeBps = 7.5;

        string? quotesPath = null;
        try
        {
            var bars = await client.RequestHistoricalBarsAsync(contract, barSize, lookback, ct).ConfigureAwait(false);
            if (bars.Count == 0)
            {
                return Fail(
                    $"No bars for {contract.Symbol} from {broker} over the last week. " +
                    "Try another symbol from Quick Backtest, or connect a data source.");
            }

            quotesPath = Path.Combine(Path.GetTempPath(), $"hyp-prove-{Guid.NewGuid():N}.parquet");
            await WriteSyntheticTicksAsync(quotesPath, bars, barSize.ToTimeSpan(), tickSize, ct).ConfigureAwait(false);

            var config = new BacktestConfig(
                Contract: contract,
                TickDataPath: quotesPath,
                TickSize: tickSize,
                SlippageTicks: 1,
                ContractMultiplier: 1,
                StartingCash: 100_000,
                FeeModel: new BpsFeeModel(feeBps),
                Source: BacktestDataSource.ParquetFile);

            var strategy = option.Create(contract);
            var result = await session.RunAsync(config, strategy, risk: null, ct).ConfigureAwait(false);
            var pnl = result.EndingCash - result.StartingCash;
            var feed =
                $"Bar-synthetic L1 from {bars.Count}×{barSize} on {contract.Symbol} ({broker}) — " +
                "rough prove; use Quick Backtest for full tape when needed.";

            var msg = result.Stats is { } s
                ? $"Prove done on {contract.Symbol}: return {s.TotalReturn.ToString("P2", CultureInfo.InvariantCulture)}, " +
                  $"Sharpe {s.Sharpe.ToString("F2", CultureInfo.InvariantCulture)}, " +
                  $"max DD {s.MaxDrawdown.ToString("P2", CultureInfo.InvariantCulture)}, " +
                  $"{s.TradeCount} trades."
                : $"Prove done on {contract.Symbol}: {result.Trades.Count} trades, P&L {pnl.ToString("C2", CultureInfo.CurrentCulture)}.";

            return new AuthoringProveResult(true, msg, result.Stats, pnl, feed, result.EquityCurve);
        }
        catch (OperationCanceledException)
        {
            return Fail("Prove cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Authoring prove failed for {Id}", strategyOptionId);
            return Fail($"Prove failed: {ex.Message}");
        }
        finally
        {
            if (quotesPath is not null)
            {
                try { File.Delete(quotesPath); }
                catch (Exception ex) { logger.LogDebug(ex, "Could not delete temp prove file"); }
            }
        }
    }

    private BrokerKind PickBroker()
    {
        if (brokers.IsAvailable(BrokerKind.Binance)) return BrokerKind.Binance;
        foreach (var k in brokers.Connected)
            if (k != BrokerKind.Simulated) return k;
        if (brokers.IsAvailable(BrokerKind.Simulated)) return BrokerKind.Simulated;
        return brokers.AvailableKinds.FirstOrDefault();
    }

    private static Contract PickContract(BrokerKind broker) =>
        broker == BrokerKind.Binance
            ? new Contract("BTCUSDT", "CRYPTO", "BINANCE", "USDT", PrimaryExchange: string.Empty)
            : Contract.UsStock("AAPL");

    private static async Task WriteSyntheticTicksAsync(
        string path, IReadOnlyList<Bar> bars, TimeSpan barSpan, double tickSize, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var step = barSpan / 4;
        await using var writer = new ParquetTickWriter(path);
        foreach (var bar in bars)
        {
            ct.ThrowIfCancellationRequested();
            var path4 = bar.Close >= bar.Open
                ? new[] { bar.Open, bar.Low, bar.High, bar.Close }
                : new[] { bar.Open, bar.High, bar.Low, bar.Close };
            var sizePer = Math.Max(1, bar.Volume / 4);
            for (var i = 0; i < path4.Length; i++)
            {
                var px = path4[i];
                var ts = bar.TimestampUtc + step * i;
                await writer.WriteAsync(new Tick(ts, px - half, px + half, sizePer, sizePer), ct).ConfigureAwait(false);
            }
        }
    }

    private static AuthoringProveResult Fail(string message) =>
        new(false, message, null, 0, null, null);
}
