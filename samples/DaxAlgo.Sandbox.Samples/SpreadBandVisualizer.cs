using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>A small host-readable state snapshot produced by <see cref="SpreadBandVisualizer"/>.</summary>
public sealed record SpreadBandViewState(
    bool IsReady,
    DateTime UpdatedAtUtc,
    int SampleCount,
    double LastPrice,
    double Midpoint,
    double LowerBand,
    double UpperBand,
    bool IsOutsideBand)
{
    public static SpreadBandViewState Empty { get; } = new(
        false,
        DateTime.UnixEpoch,
        0,
        0d,
        0d,
        0d,
        0d,
        false);
}

/// <summary>
/// Computes a rolling close-price band and evaluates final bars and scoped quote midpoints against it.
/// </summary>
public sealed class SpreadBandVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string LookbackParameter = "lookback";
    public const string BandMultiplierParameter = "bandMultiplier";

    private InstrumentId _instrument;
    private int _lookback;
    private double _bandMultiplier;
    private bool _wasOutsideBand;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter,
            "Instrument",
            new InstrumentId(1),
            group: "Market"),
        StrategyParameter.Int(
            LookbackParameter,
            "Band lookback",
            20,
            min: 3,
            max: 300,
            group: "Band",
            unit: "bars"),
        StrategyParameter.Number(
            BandMultiplierParameter,
            "Band width",
            2d,
            min: 0.1d,
            max: 10d,
            step: 0.1d,
            group: "Band",
            unit: "sigma"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.Bars | StrategyDataRequirement.L1;

    public SpreadBandViewState ViewState { get; private set; } = SpreadBandViewState.Empty;

    /// <summary>
    /// What the picture is drawn from.
    ///
    /// <para><see cref="Draw"/> receives only a surface — no context, no market data — because it runs
    /// on the render thread while the data callbacks run on a pump thread that may fire far faster.
    /// So the shape of every visualizer is the same: compute in the callbacks, keep what the picture
    /// needs, and draw from that.</para>
    ///
    /// <para>Bounded on purpose. A visualizer lives as long as its window, and an unbounded history is
    /// a memory leak with a tidy name.</para>
    /// </summary>
    private const int HistoryCapacity = 240;

    private readonly List<Sample> _history = new(HistoryCapacity);

    private readonly record struct Sample(
        double Price,
        double Midpoint,
        double LowerBand,
        double UpperBand,
        bool IsOutsideBand);

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _lookback = context.Parameters.GetInt(LookbackParameter);
        _bandMultiplier = context.Parameters.GetDouble(BandMultiplierParameter);
        _wasOutsideBand = false;
        ViewState = SpreadBandViewState.Empty;
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (bar.InstrumentId == _instrument && bar.Size == BarSize.OneMinute && bar.IsFinal)
            Evaluate(bar.Close, context);

        return Task.CompletedTask;
    }

    public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (quote.InstrumentId != _instrument)
            return Task.CompletedTask;

        var recentQuotes = context.Data.RecentQuotes(_instrument, 1);
        if (recentQuotes.Count > 0)
            Evaluate(recentQuotes[^1].Mid, context);

        return Task.CompletedTask;
    }

    private void Evaluate(double price, IVisualizerContext context)
    {
        var bars = context.Data.RecentBars(_instrument, BarSize.OneMinute, _lookback);
        if (bars.Count < _lookback)
        {
            ViewState = new SpreadBandViewState(
                false,
                context.Clock.UtcNow,
                bars.Count,
                price,
                0d,
                0d,
                0d,
                false);
            _wasOutsideBand = false;
            return;
        }

        var sum = 0d;
        for (var index = 0; index < bars.Count; index++)
            sum += bars[index].Close;
        var midpoint = sum / bars.Count;

        var squaredDifferenceSum = 0d;
        for (var index = 0; index < bars.Count; index++)
        {
            var difference = bars[index].Close - midpoint;
            squaredDifferenceSum += difference * difference;
        }

        var standardDeviation = Math.Sqrt(squaredDifferenceSum / bars.Count);
        var width = _bandMultiplier * standardDeviation;
        var lowerBand = midpoint - width;
        var upperBand = midpoint + width;
        var isOutsideBand = price < lowerBand || price > upperBand;

        ViewState = new SpreadBandViewState(
            true,
            context.Clock.UtcNow,
            bars.Count,
            price,
            midpoint,
            lowerBand,
            upperBand,
            isOutsideBand);

        Record(new Sample(price, midpoint, lowerBand, upperBand, isOutsideBand));

        if (isOutsideBand && !_wasOutsideBand)
        {
            var direction = price > upperBand ? "above" : "below";
            context.Alerts.Alert(
                $"Price moved {direction} the rolling band.",
                AlertLevel.Warning,
                $"spread-band:{direction}");
        }

        _wasOutsideBand = isOutsideBand;
    }

    private void Record(Sample sample)
    {
        // Dropping the oldest costs a shift of at most HistoryCapacity elements, which is nothing at
        // this size and keeps Draw able to index the list directly — no per-frame copy.
        if (_history.Count == HistoryCapacity)
            _history.RemoveAt(0);
        _history.Add(sample);
    }

    /// <summary>
    /// Draws the band and the price that is being measured against it.
    ///
    /// <para>Pure and fast, as the contract requires: it reads the retained history and nothing else,
    /// allocates nothing per frame, and holds no state that would have to survive to the next one.</para>
    /// </summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Spread band", RenderPanelKind.Chart);

        if (_history.Count == 0)
        {
            // Say why there is no picture. A blank panel is indistinguishable from a broken one.
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
            surface.Text(8d, 20d, $"Waiting for {_lookback} bars…");
            return;
        }

        var range = PlotRange.Empty;
        for (var index = 0; index < _history.Count; index++)
        {
            var sample = _history[index];
            range = range.Include(sample.LowerBand).Include(sample.UpperBand).Include(sample.Price);
        }
        range = range.Padded();

        // Grid first so everything else draws over it; this declares the Y axis as a side effect.
        Plot.HorizontalGrid(surface, range);
        surface.AxisX(0d, Math.Max(1, _history.Count - 1));

        // Theme roles rather than literal colours, so the picture stays legible in every theme.
        Band(surface, range, RenderThemeColor.Neutral);
        Line(surface, "Midpoint", RenderThemeColor.Accent, sample => sample.Midpoint);
        Line(surface, "Price", RenderThemeColor.Text, sample => sample.Price);

        // Mark the breaches — the whole point of the band.
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Warning)));
        for (var index = 0; index < _history.Count; index++)
        {
            if (_history[index].IsOutsideBand)
                surface.Marker(index, _history[index].Price, RenderMarkerShape.Circle);
        }

        Plot.Crosshair(surface, range);

        void Band(IRenderSurface target, PlotRange bounds, RenderThemeColor color)
        {
            _ = bounds;
            target.SetStyle(new RenderStyle(target.Theme(color), Dashed: true));
            Line(target, "Upper band", color, sample => sample.UpperBand, dashed: true);
            Line(target, "Lower band", color, sample => sample.LowerBand, dashed: true);
        }

        void Line(
            IRenderSurface target,
            string name,
            RenderThemeColor color,
            Func<Sample, double> select,
            bool dashed = false)
        {
            target.SetStyle(new RenderStyle(target.Theme(color), Dashed: dashed));
            using var series = target.Series(name, RenderSeriesKind.Line);
            for (var index = 0; index < _history.Count; index++)
                target.Push(index, select(_history[index]));
        }
    }
}
