using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// LuxAlgo-inspired confirmation / contrarian markers on a price chart — the authored-unit home
/// for signal overlays (SDK <see cref="PriceChart"/> + <see cref="LuxSignalMarkers"/>), as opposed
/// to the Charts WebView toggle.
/// </summary>
public sealed class LuxSignalsVisualizer : IVisualizer
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

    public UnitLayout Layout => UnitLayout.Panel("Lux-style signals", DrawChart).Star();

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
        var (markers, trail) = LuxSignalMarkers.FromBars(slice);
        SeriesData[]? overlays = trail.Any(v => double.IsFinite(v) && v > 0)
            ? [SeriesData.Line("Smart trail", trail, RenderThemeColor.Accent)]
            : null;

        PriceChart.Draw(
            surface,
            slice,
            PriceChartOptions.Default,
            overlays: overlays,
            markers: markers);
    }
}
