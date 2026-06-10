using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.AdvancedRegime;

namespace TradingTerminal.Infrastructure.Regime.AdvancedRegime;

/// <summary>
/// Default <see cref="IAdvancedRegimeProvider"/> implementation. Pulls bars from
/// <see cref="IMarketDataRepository"/> on the named broker and resamples them per requested
/// timeframe: a single 1-minute base series feeds every intraday column (aggregated via
/// <see cref="BarTimeframeAggregator"/>), while the daily column fetches 1-day bars directly.
/// The pure-math <see cref="AdvancedRegimeCalculator"/> does the rest. Like
/// <c>InstrumentRegimeService</c> this degrades on failure rather than throwing — a failed fetch
/// folds into a stable, neutral snapshot.
/// </summary>
public sealed class AdvancedRegimeService : IAdvancedRegimeProvider
{
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    /// <summary>Longest indicator lookback (MA50 / ATR-regression / POC) plus headroom.</summary>
    private const int RequiredBuckets = 60;

    private readonly IMarketDataRepository _repository;
    private readonly ILogger<AdvancedRegimeService> _logger;

    public AdvancedRegimeService(
        IMarketDataRepository repository,
        ILogger<AdvancedRegimeService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<AdvancedRegimeSnapshot> AnalyseAsync(
        Contract contract,
        BrokerKind broker,
        string displaySymbol,
        IReadOnlyList<AdvancedTimeframe> timeframes,
        AdvancedRegimeSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings ??= AdvancedRegimeSettings.Default;

        // 1. Filter to enabled timeframes. Nothing enabled → empty-with-symbol.
        var enabled = (timeframes ?? Array.Empty<AdvancedTimeframe>())
            .Where(tf => tf.Enabled)
            .ToList();
        if (enabled.Count == 0)
            return AdvancedRegimeSnapshot.Empty with { Symbol = displaySymbol, GeneratedAtUtc = DateTime.UtcNow };

        var intraday = enabled.Where(tf => tf.Bucket < OneDay).ToList();
        var daily = enabled.Where(tf => tf.Bucket >= OneDay).ToList();

        // 2. Intraday base series: one fetch of 1m bars, sized so the coarsest enabled intraday
        //    bucket has >= RequiredBuckets buckets. Failure degrades to an empty base series.
        IReadOnlyList<Bar> baseBars = Array.Empty<Bar>();
        if (intraday.Count > 0)
        {
            try
            {
                var duration = IntradayDuration(intraday);
                baseBars = await _repository.GetHistoricalBarsAsync(
                    contract, broker, BarSize.OneMinute, duration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Advanced regime: 1m base fetch failed for {Symbol} on {Broker}", displaySymbol, broker);
                baseBars = Array.Empty<Bar>();
            }
        }

        // 3. Daily series: fetch 1D bars directly (never aggregate 1m up to a day). Failure degrades
        //    to an empty series so the column still renders (calculator emits "—" cells).
        IReadOnlyList<Bar> dailyBars = Array.Empty<Bar>();
        if (daily.Count > 0)
        {
            try
            {
                var dayDuration = TimeSpan.FromDays(RequiredBuckets * 1.5);
                dailyBars = await _repository.GetHistoricalBarsAsync(
                    contract, broker, BarSize.OneDay, dayDuration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Advanced regime: 1D fetch failed for {Symbol} on {Broker}", displaySymbol, broker);
                dailyBars = Array.Empty<Bar>();
            }
        }

        // If every column's source failed, return empty-with-symbol rather than a wall of "—".
        if (baseBars.Count == 0 && dailyBars.Count == 0)
            return AdvancedRegimeSnapshot.Empty with { Symbol = displaySymbol, GeneratedAtUtc = DateTime.UtcNow };

        // 4. Build one column per enabled timeframe, preserving the requested order.
        //    Native pass-through where the bucket matches the fetched size; otherwise aggregate.
        var columns = new List<(AdvancedTimeframe Timeframe, IReadOnlyList<Bar> Bars)>(enabled.Count);
        foreach (var tf in enabled)
        {
            IReadOnlyList<Bar> bars;
            if (tf.Bucket >= OneDay)
                bars = dailyBars; // daily column uses the 1D series directly
            else if (tf.Bucket <= TimeSpan.FromMinutes(1))
                bars = baseBars;  // 1m column is the native base series
            else
                bars = BarTimeframeAggregator.Aggregate(baseBars, tf.Bucket);

            columns.Add((tf, bars));
        }

        // 5. Pure calculator turns the per-column series into the snapshot.
        return AdvancedRegimeCalculator.Compute(displaySymbol, columns, settings, DateTime.UtcNow);
    }

    /// <summary>
    /// 1m-bar duration sized so the coarsest enabled intraday bucket holds at least
    /// <see cref="RequiredBuckets"/> buckets, rounded up to whole days and capped for sanity.
    /// </summary>
    private static TimeSpan IntradayDuration(IReadOnlyList<AdvancedTimeframe> intraday)
    {
        var coarsest = intraday.Max(tf => tf.Bucket);

        // Minutes of history needed = buckets * bucket-minutes; round up to whole days.
        var neededMinutes = RequiredBuckets * coarsest.TotalMinutes;
        var neededDays = Math.Ceiling(neededMinutes / (24 * 60.0));

        // Cap: 5 days is ample for <=30m coarsest; allow more (10) once 1H is in the mix so the
        // 1H column gets its >=60 hours without trimming.
        var capDays = coarsest >= TimeSpan.FromHours(1) ? 10 : 5;
        var days = Math.Clamp(neededDays, 1, capDays);
        return TimeSpan.FromDays(days);
    }
}
