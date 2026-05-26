using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Regime.Instrument;

namespace TradingTerminal.Infrastructure.Regime.Instrument;

/// <summary>
/// Default <see cref="IInstrumentRegimeProvider"/> implementation. Pulls recent OHLCV bars
/// from <see cref="IMarketDataRepository"/> on the named broker and, if that broker exposes
/// L2, grabs one short-lived <see cref="DepthSnapshot"/> via the named broker's
/// <see cref="IBrokerClient"/>. The pure-math <see cref="InstrumentRegimeCalculator"/> does
/// the rest.
/// </summary>
public sealed class InstrumentRegimeService : IInstrumentRegimeProvider
{
    private static readonly TimeSpan DepthSnapshotTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<InstrumentRegimeService> _logger;

    public InstrumentRegimeService(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<InstrumentRegimeService> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;
    }

    public async Task<InstrumentRegimeSnapshot> AnalyseAsync(
        Contract contract,
        BrokerKind broker,
        string displaySymbol,
        BarSize timeframe,
        int barCount,
        CancellationToken ct = default)
    {
        // 1. Bars — required. Failure to fetch leaves an empty snapshot, not an exception.
        IReadOnlyList<Bar> bars;
        try
        {
            var duration = EstimateDuration(timeframe, barCount);
            bars = await _repository.GetHistoricalBarsAsync(contract, broker, timeframe, duration, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Instrument regime: bar fetch failed for {Symbol} on {Broker}", displaySymbol, broker);
            return InstrumentRegimeSnapshot.Empty with { Symbol = displaySymbol, Timeframe = timeframe, GeneratedAtUtc = DateTime.UtcNow };
        }

        // 2. Depth — optional. Brokers without L2 throw NotSupportedException; we degrade silently.
        var depth = await TryGetDepthAsync(contract, broker, ct);

        var inputs = new InstrumentRegimeInputs(displaySymbol, timeframe, bars, depth);
        return InstrumentRegimeCalculator.Compute(inputs, DateTime.UtcNow);
    }

    private async Task<DepthSnapshot?> TryGetDepthAsync(Contract contract, BrokerKind broker, CancellationToken outerCt)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            cts.CancelAfter(DepthSnapshotTimeout);
            await foreach (var snap in _selector.Get(broker).SubscribeDepthAsync(contract, levels: 10, cts.Token))
            {
                return snap; // first snapshot is sufficient
            }
        }
        catch (NotSupportedException)
        {
            // IB / NT / Alpaca don't yet emit depth — expected path.
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            // Timeout: broker is wired but the symbol may not have depth available.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Instrument regime: depth fetch skipped for {Symbol} on {Broker}", contract.Symbol, broker);
        }
        return null;
    }

    private static TimeSpan EstimateDuration(BarSize size, int barCount) => size switch
    {
        BarSize.OneMinute      => TimeSpan.FromMinutes(barCount * 1.5),
        BarSize.ThreeMinutes   => TimeSpan.FromMinutes(barCount * 3 * 1.5),
        BarSize.FiveMinutes    => TimeSpan.FromMinutes(barCount * 5 * 1.5),
        BarSize.FifteenMinutes => TimeSpan.FromMinutes(barCount * 15 * 1.5),
        BarSize.OneHour        => TimeSpan.FromHours(barCount * 1.5),
        BarSize.OneDay         => TimeSpan.FromDays(barCount * 1.5),
        _                      => TimeSpan.FromDays(7),
    };
}
