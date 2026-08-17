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
/// Hyperion Prove: real historical trade tape only (same write path as Quick Backtest full-tape).
/// No bar→synthetic L1 fallback — if the broker cannot return prints, Prove fails honestly.
/// </summary>
public sealed class AuthoringProveService(
    IBacktestStrategyRegistry registry,
    IBacktestSession session,
    IBrokerSelector brokers,
    ILogger<AuthoringProveService> logger) : IAuthoringProveService
{
    private const int MaxTrades = 200_000;
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

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
            return Fail("No market-data broker is available. Connect Binance (historical trades), then retry.");

        var client = brokers.Get(broker);
        var contract = PickContract(broker);
        var tickSize = 0.01;
        var feeBps = 7.5;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc - Lookback;

        string? quotesPath = null;
        string? tradesPath = null;
        try
        {
            IReadOnlyList<TradeTick> tape;
            try
            {
                tape = await client.RequestHistoricalTradesAsync(contract, fromUtc, toUtc, MaxTrades, ct)
                    .ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                return Fail(
                    $"{broker} has no historical trade tape. Connect Binance (or another tape broker). " +
                    "Hyperion Prove does not synthesize ticks from bars.");
            }

            if (tape.Count == 0)
            {
                return Fail(
                    $"No real trades for {contract.Symbol} from {broker} over the last {Lookback.TotalDays:0} days. " +
                    "Try a more liquid symbol — Prove will not invent a tape from bars.");
            }

            quotesPath = Path.Combine(Path.GetTempPath(), $"hyp-prove-q-{Guid.NewGuid():N}.parquet");
            tradesPath = Path.Combine(Path.GetTempPath(), $"hyp-prove-t-{Guid.NewGuid():N}.parquet");
            await WriteRealTapeAsync(quotesPath, tradesPath, tape, tickSize, broker, ct).ConfigureAwait(false);

            var config = new BacktestConfig(
                Contract: contract,
                TickDataPath: quotesPath,
                TickSize: tickSize,
                SlippageTicks: 1,
                ContractMultiplier: 1,
                StartingCash: 100_000,
                FeeModel: new BpsFeeModel(feeBps),
                Source: BacktestDataSource.ParquetFile,
                TradeDataPath: tradesPath);

            var strategy = option.Create(contract);
            var result = await session.RunAsync(config, strategy, risk: null, ct).ConfigureAwait(false);
            var pnl = result.EndingCash - result.StartingCash;
            var fidelity = AuthoringFidelityStrip.ForRealTapeRun(contract.Symbol, broker.ToString(), tape.Count);
            var feed = fidelity.Detail;

            var msg = result.Stats is { } s
                ? $"Prove done on {contract.Symbol} ({tape.Count:N0} real prints): return {s.TotalReturn.ToString("P2", CultureInfo.InvariantCulture)}, " +
                  $"Sharpe {s.Sharpe.ToString("F2", CultureInfo.InvariantCulture)}, " +
                  $"max DD {s.MaxDrawdown.ToString("P2", CultureInfo.InvariantCulture)}, " +
                  $"{s.TradeCount} trades."
                : $"Prove done on {contract.Symbol}: {result.Trades.Count} trades, P&L {pnl.ToString("C2", CultureInfo.CurrentCulture)}.";

            return new AuthoringProveResult(true, msg, result.Stats, pnl, feed, result.EquityCurve, fidelity);
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
            TryDelete(quotesPath);
            TryDelete(tradesPath);
        }
    }

    private BrokerKind PickBroker()
    {
        // Prefer brokers that typically expose historical trades.
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

    /// <summary>Same real-tape parquet pair as Quick Backtest — genuine prints + L1 straddle for fills.</summary>
    private static async Task WriteRealTapeAsync(
        string quotesPath, string tradesPath, IReadOnlyList<TradeTick> tape, double tickSize, BrokerKind source, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var lastTicks = long.MinValue;

        await using var quoteWriter = new ParquetTickWriter(quotesPath);
        await using var tradeWriter = new ParquetTradeWriter(tradesPath);

        long seq = 0;
        foreach (var p in tape)
        {
            ct.ThrowIfCancellationRequested();

            var ts = p.TimestampUtc;
            if (ts.Ticks <= lastTicks) ts = new DateTime(lastTicks + 10, DateTimeKind.Utc);
            lastTicks = ts.Ticks;

            var sizeProxy = Math.Max(1, p.Size);
            await quoteWriter.WriteAsync(new Tick(ts, p.Price - half, p.Price + half, sizeProxy, sizeProxy), ct)
                .ConfigureAwait(false);
            await tradeWriter.WriteAsync(
                    new TradePrint(InstrumentId.None, ts, ts, p.Price, p.Size, p.Aggressor, source, seq++, EventTimeApproximate: false),
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch { /* best-effort temp cleanup */ }
    }

    private static AuthoringProveResult Fail(string message) =>
        new(false, message, null, 0, null, null, AuthoringFidelityStrip.ProveDefault);
}
