using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Correlation;

/// <summary>
/// Drives the (historical) Correlation Matrix window. On top of the shared instrument picker and
/// grid in <see cref="CorrelationPickerViewModelBase"/>, this adds a timeframe + lookback picker
/// and an on-demand Compute that pulls historical bars per instrument
/// (<see cref="IMarketDataRepository.GetHistoricalBarsAsync"/> — cache-first, then a broker fetch on
/// miss), aligns them by timestamp, and renders the NxN Pearson-on-log-returns matrix via
/// <see cref="CorrelationCalculator"/>. For a continuously-updating variant see
/// <see cref="LiveCorrelationMatrixViewModel"/>.
/// </summary>
public sealed partial class CorrelationMatrixViewModel : CorrelationPickerViewModelBase, IDisposable
{
    // Brokers (IB especially) reject simultaneous historical-data bursts with pacing-violation
    // errors, so a single Compute that fired one reqHistoricalData per instrument at once would
    // see a random subset fail. We cap how many are in flight and retry the ones that hit a
    // rate limit, so the user never has to reclick to "fill in" the misses.
    private const int MaxConcurrentFetches = 3;

    private CancellationTokenSource? _runCts;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("5 min",  BarSize.FiveMinutes),
        new("15 min", BarSize.FifteenMinutes),
        new("1 hour", BarSize.OneHour),
        new("1 day",  BarSize.OneDay),
    };

    private static readonly IReadOnlyList<LookbackOption> AllLookbacks = new LookbackOption[]
    {
        new("30 days",  TimeSpan.FromDays(30)),
        new("90 days",  TimeSpan.FromDays(90)),
        new("180 days", TimeSpan.FromDays(180)),
        new("1 year",   TimeSpan.FromDays(365)),
    };

    public CorrelationMatrixViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<CorrelationMatrixViewModel> logger)
        : base(repository, selector, logger)
    {
        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneDay);

        Lookbacks = new ObservableCollection<LookbackOption>(AllLookbacks);
        SelectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromDays(90));
    }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<LookbackOption> Lookbacks { get; }

    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private LookbackOption? _selectedLookback;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task ComputeAsync()
    {
        if (IsBusy) return;

        var selected = SelectedInstruments;
        if (selected.Count < 2)
        {
            StatusMessage = "Select at least two instruments.";
            return;
        }
        if (SelectedTimeframe is null || SelectedLookback is null)
        {
            StatusMessage = "Pick a timeframe and lookback.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Fetching {selected.Count} instruments…";
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var barSize = SelectedTimeframe.BarSize;
            var duration = SelectedLookback.Duration;

            var results = await FetchAllAsync(selected, barSize, duration, ct);

            // One automatic retry for transient (rate-limit) misses, run sequentially so the
            // retry itself can't re-trigger the burst that caused them.
            var transient = results.Where(f => f.Transient).Select(f => f.Instrument).ToList();
            if (transient.Count > 0 && !ct.IsCancellationRequested)
            {
                StatusMessage = $"Rate-limited on {transient.Count} — retrying…";
                for (int i = 0; i < results.Count; i++)
                {
                    if (!results[i].Transient) continue;
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    var retry = await FetchBarsAsync(results[i].Instrument, barSize, duration, ct);
                    if (retry.SkipReason is null || !retry.Transient)
                        results[i] = retry;
                }
            }

            var usable = results.Where(f => f.SkipReason is null).ToList();
            var skipped = results.Where(f => f.SkipReason is not null)
                .Select(f => $"{f.Instrument.Symbol} ({f.SkipReason})")
                .ToList();

            if (usable.Count < 2)
            {
                StatusMessage = "Not enough historical data to correlate (need ≥2 instruments with bars)."
                    + (skipped.Count > 0 ? $" Skipped: {string.Join(", ", skipped)}." : string.Empty);
                Labels.Clear();
                MatrixRows.Clear();
                SampleCount = 0;
                return;
            }

            var result = await Task.Run(() =>
            {
                var series = usable.Select(u => (IReadOnlyList<Bar>)u.Bars).ToList();
                var (timestamps, aligned) = CorrelationCalculator.AlignByTimestamp(series);
                var returns = aligned.Select(a => CorrelationCalculator.LogReturns(a)).ToList();
                var matrix = CorrelationCalculator.PearsonMatrix(returns);
                int samples = timestamps.Count > 0 ? timestamps.Count - 1 : 0;
                var labels = LabelFor(usable.Select(u => u.Instrument).ToList());
                return new CorrelationMatrix(labels, matrix, samples);
            }, ct);

            ct.ThrowIfCancellationRequested();
            BuildMatrix(result);

            StatusMessage = result.SampleCount < 20
                ? $"Computed over {result.SampleCount} bars (low sample — treat with caution)."
                : $"Computed over {result.SampleCount} aligned bars.";
            if (skipped.Count > 0)
                StatusMessage += $" Skipped: {string.Join(", ", skipped)}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Correlation matrix compute failed");
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Fetches every selected instrument with a bounded number in flight at once. Firing one
    /// historical request per instrument simultaneously trips broker pacing limits (IB error 162),
    /// so a single Compute would otherwise see a random subset fail. The semaphore caps the burst;
    /// the per-instrument result order is preserved so callers can do an indexed retry pass.
    /// </summary>
    private async Task<List<FetchResult>> FetchAllAsync(
        IReadOnlyList<SelectableInstrument> instruments, BarSize barSize, TimeSpan duration, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentFetches);
        async Task<FetchResult> Gated(SelectableInstrument inst)
        {
            await gate.WaitAsync(ct);
            try { return await FetchBarsAsync(inst, barSize, duration, ct); }
            finally { gate.Release(); }
        }

        var results = await Task.WhenAll(instruments.Select(Gated));
        return results.ToList();
    }

    /// <summary>
    /// Fetches bars for one instrument from <em>its own</em> broker (contracts are broker-specific,
    /// so we never fan a broker-B contract at broker A). The repository is cache-first and fetches
    /// from the broker on a miss, so "data not present locally" resolves itself as long as the
    /// instrument's broker is connected. Returns a non-null <see cref="FetchResult.SkipReason"/>
    /// when the row can't contribute (and flags <see cref="FetchResult.Transient"/> for failures
    /// worth retrying, e.g. a rate limit), so Compute can report exactly why and retry the misses.
    /// </summary>
    private async Task<FetchResult> FetchBarsAsync(
        SelectableInstrument inst, BarSize barSize, TimeSpan duration, CancellationToken ct)
    {
        if (!Selector.IsConnected(inst.Broker))
            return new FetchResult(inst, Array.Empty<Bar>(), $"{inst.BrokerAbbrev} not connected");

        try
        {
            var bars = await Repository.GetHistoricalBarsAsync(inst.Contract, inst.Broker, barSize, duration, ct);
            if (bars is null || bars.Count < 2)
                return new FetchResult(inst, bars ?? Array.Empty<Bar>(), "no data from broker");
            return new FetchResult(inst, bars, SkipReason: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A broker fetch can fail transiently (IB pacing, a momentary disconnect). Mark it
            // retryable so Compute can take a second pass instead of the user reclicking.
            Logger.LogWarning(ex, "Correlation matrix: bars unavailable for {Symbol} on {Broker}", inst.Symbol, inst.Broker);
            return new FetchResult(inst, Array.Empty<Bar>(), "fetch failed", Transient: true);
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
    }
}

/// <summary>Per-instrument fetch outcome. <see cref="SkipReason"/> is null when the bars are
/// usable; otherwise it's a short human-readable reason surfaced in the status line.
/// <see cref="Transient"/> marks failures worth one automatic retry (e.g. a broker rate limit).</summary>
internal sealed record FetchResult(
    SelectableInstrument Instrument, IReadOnlyList<Bar> Bars, string? SkipReason, bool Transient = false);

/// <summary>Bar-size dropdown row.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}

/// <summary>Lookback-window dropdown row.</summary>
public sealed record LookbackOption(string Label, TimeSpan Duration)
{
    public override string ToString() => Label;
}
