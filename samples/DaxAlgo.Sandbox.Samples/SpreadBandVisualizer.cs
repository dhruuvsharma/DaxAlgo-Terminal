using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>The retained snapshot the picture is drawn from, and what a test can assert on without a
/// render surface.</summary>
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
/// A rolling price band with the statistics a desk actually reads beside it.
///
/// <para><b>This is the visualizer exemplar Hyperion is shown</b>, so it is written the way a generated
/// unit should be rather than the smallest thing that works. Three habits are on display and all three
/// are the difference between a chart and a tool:</para>
///
/// <list type="number">
///   <item><b>The maths comes from <c>DaxAlgo.Sdk.Quant</c>.</b> Not one loop here computes a mean or a
///     standard deviation. <see cref="BollingerBands"/>, <see cref="ZScore"/>,
///     <see cref="RealizedVolatility"/> and <see cref="SpreadStats"/> are streaming, warm-up gated and
///     already tested — hand-rolling them costs output tokens and buys a chance of being subtly wrong
///     in a way nothing downstream can catch.</item>
///   <item><b>The picture comes from the widget library.</b> <c>Bands</c>, <c>Series.Chart</c>,
///     <c>Signals</c> and <c>Tiles</c> instead of raw <c>Push</c> loops, so the legend, the grid, the
///     axis formatting and the marker shapes are the same as in every other window.</item>
///   <item><b>It says what the numbers mean.</b> A strip of tiles — where price sits in the band, how
///     wide the band is, realised volatility, whether the spread is unusual — turns a line chart into
///     something worth leaving open. Most generated visualizers stop at the line.</item>
/// </list>
///
/// <para>The window is two panels, declared as a <see cref="UnitLayout"/>: the chart takes the space,
/// the statistics take a fixed strip. <see cref="Draw"/> composes the same two into one surface with
/// <c>PlotArea</c>, which is the distinction worth learning — <c>PlotArea</c> divides a picture,
/// <c>UnitLayout</c> divides a window.</para>
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

    // Constructed in OnStartAsync, once the parameters are known. Every one of these is O(1) per
    // update and gates itself on IsReady — no window is re-scanned, and nothing reads before it means
    // anything.
    private BollingerBands? _band;
    private ZScore? _stretch;
    private RealizedVolatility? _volatility;
    private readonly SpreadStats _spread = new(200);

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
    /// Two panels: the band chart, and a statistics strip pinned under it.
    ///
    /// <para>A fixed height for the strip and a star for the chart, because the tiles need exactly as
    /// much room as they need and everything left belongs to the price.</para>
    /// </summary>
    public UnitLayout Layout => UnitLayout.Of(DaxAlgo.Sdk.Layout.Layout.Rows(
        DaxAlgo.Sdk.Layout.Layout.Panel("Band", DrawChart).Star(4),
        DaxAlgo.Sdk.Layout.Layout.Panel("Statistics", DrawStats).Pixels(64)));

    /// <summary>Bounded on purpose. A visualizer lives as long as its window, and an unbounded history
    /// is a memory leak with a tidy name.</summary>
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

        _band = new BollingerBands(_lookback, _bandMultiplier);
        _stretch = new ZScore(_lookback, minimumSamples: Math.Min(_lookback, 10));
        _volatility = new RealizedVolatility(_lookback);

        _wasOutsideBand = false;
        _history.Clear();
        _spread.Reset();
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

        // The spread's own distribution, so "unusually wide" is measured in this instrument's terms
        // rather than in a tick count that is wrong on the next one.
        _spread.Update(quote);

        var recentQuotes = context.Data.RecentQuotes(_instrument, 1);
        if (recentQuotes.Count > 0)
            Evaluate(recentQuotes[^1].Mid, context);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Folds one price into the estimators and records what the picture needs.
    ///
    /// <para>The band is fed from BARS and evaluated against the latest price, which may be a quote
    /// mid. That is deliberate: a band recomputed on every quote would tighten around the quote noise
    /// it is meant to measure.</para>
    /// </summary>
    private void Evaluate(double price, IVisualizerContext context)
    {
        if (_band is null || _stretch is null || _volatility is null) return;

        var bars = context.Data.RecentBars(_instrument, BarSize.OneMinute, _lookback);
        if (bars.Count < _lookback)
        {
            ViewState = SpreadBandViewState.Empty with
            {
                UpdatedAtUtc = context.Clock.UtcNow,
                SampleCount = bars.Count,
                LastPrice = price,
            };
            _wasOutsideBand = false;
            return;
        }

        // Rebuilt from the host's own history rather than accumulated, so the picture is correct after
        // a gap in the feed and after the look-back is changed at runtime. An estimator fed a stream
        // it has already seen would double-count.
        _band = new BollingerBands(_lookback, _bandMultiplier);
        _volatility = new RealizedVolatility(_lookback);
        foreach (var seen in bars)
        {
            _band.Update(seen.Close);
            _volatility.Update(seen.Close);
        }

        var midpoint = _band.Middle;
        var lowerBand = _band.Lower;
        var upperBand = _band.Upper;
        var isOutsideBand = price < lowerBand || price > upperBand;

        _stretch.Update(price);

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
    /// The whole dashboard on one surface — the fallback for a host that does not build
    /// <see cref="Layout"/>, and what the authoring preview renders.
    ///
    /// <para>Same two pictures, divided with <c>PlotArea</c> instead of into real panels. That is the
    /// difference between the two vocabularies in one method: <c>PlotArea</c> splits a rectangle,
    /// <c>UnitLayout</c> splits a window into panels with their own headers and draggable
    /// separators.</para>
    ///
    /// <para>Pure and fast, as the contract requires: it reads the retained history and nothing else,
    /// and holds no state that would have to survive to the next frame.</para>
    /// </summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        using var panel = surface.Panel("Spread band", RenderPanelKind.Chart);
        if (Waiting(surface)) return;

        // (Taken, Remainder) — SplitBottom hands back the STRIP FIRST and the rest second, so the
        // stats strip is the first of the pair. Named the other way round it compiles, draws, and puts
        // the chart in 58 pixels at the bottom with the tiles filling the panel above it.
        var (stats, chart) = PlotArea.Of(surface).SplitBottom(58d);
        DrawChart(surface, chart);
        DrawStats(surface, stats);
    }

    private void DrawChart(IRenderSurface surface) => DrawChart(surface, PlotArea.Of(surface));

    private void DrawChart(IRenderSurface surface, PlotArea area)
    {
        if (_history.Count == 0)
        {
            Plot.Waiting(surface, $"Waiting for {_lookback} bars…");
            return;
        }

        // The envelope first, so the price draws over it. Bands fills between the edges rather than
        // stroking two lines, which is what makes the region read as one thing.
        var range = Bands.Draw(
            surface,
            Column(static sample => sample.UpperBand),
            Column(static sample => sample.LowerBand),
            Column(static sample => sample.Midpoint),
            area: area);

        // One scale for both, with the grid, axes, legend and crosshair that belong to it. Drawn
        // separately each would fill the panel and look like they never diverged.
        Series.Chart(
            surface,
            [
                SeriesData.Dashed("Midpoint", Column(static sample => sample.Midpoint), RenderThemeColor.Neutral),
                SeriesData.Line("Price", Column(static sample => sample.Price), RenderThemeColor.Text),
            ],
            area: area);

        // The breaches are the signal, so they are what the eye should land on. Signals draws shape as
        // well as colour, which is what makes them readable to the roughly one man in twelve who
        // cannot separate the bullish and bearish roles reliably.
        var marks = new List<Signal>();
        for (var index = 0; index < _history.Count; index++)
        {
            if (!_history[index].IsOutsideBand) continue;

            marks.Add(new Signal(
                index,
                _history[index].Price,
                _history[index].Price > _history[index].UpperBand ? SignalKind.Sell : SignalKind.Buy));
        }

        Signals.Draw(surface, marks, _history.Count, range, area: area);
        Plot.Crosshair(surface, range, area: area);
    }

    /// <summary>
    /// The numbers, as tiles.
    ///
    /// <para>Every one is normalised — position within the band, band width as a fraction, volatility
    /// per bar, the spread in standard deviations of its own history — so the strip reads the same on
    /// any instrument. A tile showing an absolute price band width would mean nothing without knowing
    /// the symbol.</para>
    /// </summary>
    private void DrawStats(IRenderSurface surface) => DrawStats(surface, PlotArea.Of(surface));

    private void DrawStats(IRenderSurface surface, PlotArea area)
    {
        if (_band is null || _stretch is null || _volatility is null || !ViewState.IsReady)
        {
            Plot.Caption(surface, area, "Statistics appear once the band has enough bars.");
            return;
        }

        var stretch = _stretch.IsReady ? _stretch.Value : 0d;

        Tiles.Draw(
            surface,
            [
                new Tile(
                    "In band",
                    _band.PercentB.ToString("P0"),
                    ViewState.IsOutsideBand ? "outside" : "inside",
                    ViewState.IsOutsideBand ? RenderThemeColor.Warning : RenderThemeColor.Text),
                Tile.Signed("Stretch", stretch, stretch.ToString("F2"), "sigma from mean"),
                new Tile("Band width", _band.Width.ToString("P2"), "of midpoint"),
                new Tile(
                    "Volatility",
                    _volatility.IsReady ? _volatility.Value.ToString("P2") : "—",
                    "per bar"),
                new Tile(
                    "Spread",
                    _spread.IsReady ? _spread.ZScore.ToString("F1") : "—",
                    _spread.IsWide() ? "unusually wide" : "normal",
                    _spread.IsWide() ? RenderThemeColor.Warning : RenderThemeColor.Text),
            ],
            area: area);
    }

    /// <summary>Says why there is no picture. A blank panel is indistinguishable from a broken one,
    /// and this is the state a user sees for the first seconds of every session.</summary>
    private bool Waiting(IRenderSurface surface)
    {
        if (_history.Count > 0) return false;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
        surface.Text(8d, 20d, $"Waiting for {_lookback} bars…");
        return true;
    }

    /// <summary>One field of the sample history as a plain column, which is what the widgets take.</summary>
    private double[] Column(Func<Sample, double> select)
    {
        var values = new double[_history.Count];
        for (var index = 0; index < _history.Count; index++) values[index] = select(_history[index]);
        return values;
    }
}
