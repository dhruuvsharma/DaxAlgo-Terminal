using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// Modern Ichimoku Cloud — classic Hosoda lines + ATR-graded Kumo + qualified TK/Kumo markers.
/// Hyperion exemplar for ichimoku / kumo / cloud briefs (GBB-inspired, not a Pine clone).
/// </summary>
public sealed class ModernIchimokuVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string BarsVisibleParameter = "barsVisible";

    private const int Capacity = 400;

    private InstrumentId _instrument;
    private int _barsVisible;
    private readonly List<OhlcvBar> _bars = [];

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Int(BarsVisibleParameter, "Bars visible", 120, min: 40, max: 400, group: "Chart", unit: "bars"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    public UnitLayout Layout => UnitLayout.Panel("Modern Ichimoku", DrawChart).Star();

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _barsVisible = context.Parameters.GetInt(BarsVisibleParameter);
        _bars.Clear();
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.InstrumentId != _instrument) return Task.CompletedTask;
        _bars.Add(bar);
        while (_bars.Count > Capacity) _bars.RemoveAt(0);
        return Task.CompletedTask;
    }

    private void DrawChart(IRenderSurface surface)
    {
        if (_bars.Count == 0)
        {
            Plot.Waiting(surface, "Waiting for bars…");
            return;
        }

        var window = Math.Min(_barsVisible, _bars.Count);
        var slice = _bars.Skip(_bars.Count - window).ToList();
        var series = ModernIchimokuChart.FromBars(slice);

        var overlays = new List<SeriesData>
        {
            SeriesData.Line("Tenkan", series.Tenkan, RenderThemeColor.Accent),
            SeriesData.Line("Kijun", series.Kijun, RenderThemeColor.Warning),
            SeriesData.Line("Span A", series.SpanA, RenderThemeColor.Bullish),
            SeriesData.Line("Span B", series.SpanB, RenderThemeColor.Bearish),
            SeriesData.Dashed("Chikou", series.Chikou, RenderThemeColor.Neutral),
        };

        var levels = new List<Level>();
        if (double.IsFinite(series.FlatKijun))
            levels.Add(new Level(series.FlatKijun, "Flat Kijun", RenderThemeColor.Warning));
        if (double.IsFinite(series.FlatSenkouB))
            levels.Add(new Level(series.FlatSenkouB, "Flat Senkou B", RenderThemeColor.Bearish));

        var layout = PriceChart.Draw(
            surface,
            slice,
            PriceChartOptions.Default,
            overlays: overlays,
            markers: series.Markers,
            levels: levels);

        if (!layout.IsValid) return;

        // Cloud fill between Span A and B (ATR grade drives opacity).
        var fill = series.LastGrade switch
        {
            ModernIchimoku.ThicknessGrade.Thin => 0.06d,
            ModernIchimoku.ThicknessGrade.Normal => 0.12d,
            ModernIchimoku.ThicknessGrade.Thick => 0.18d,
            ModernIchimoku.ThicknessGrade.VeryThick => 0.26d,
            _ => 0.10d,
        };
        Bands.Draw(
            surface,
            series.SpanA,
            series.SpanB,
            options: new BandOptions(RenderThemeColor.Accent, FillAlpha: fill, EdgeAlpha: 0.35d, ShowMiddle: false),
            range: layout.Range,
            area: layout.Price);

        var grade = series.LastGrade == ModernIchimoku.ThicknessGrade.Unknown
            ? "—"
            : series.LastGrade.ToString();
        var thick = double.IsFinite(series.LastThicknessAtr) ? series.LastThicknessAtr.ToString("0.00") : "—";
        var dist = double.IsFinite(series.LastPriceToCloudAtr) ? series.LastPriceToCloudAtr.ToString("0.00") : "—";
        Plot.Caption(surface, layout.Price, $"Kumo {grade} · thick {thick} ATR · dist {dist} ATR");
    }

}
