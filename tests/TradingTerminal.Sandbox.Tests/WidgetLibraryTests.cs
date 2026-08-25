using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The contract every widget in the library owes, applied to all of them at once.
///
/// <para>The library exists so Hyperion plugs in a picture instead of writing one, which only pays off
/// if plugging one in cannot go wrong in the ways hand-written drawing code does. Four failures matter,
/// and they are the ones a generated visualizer actually produces:</para>
///
/// <list type="number">
///   <item><b>It draws nothing.</b> A blank panel reads as a broken application, and it is invisible in
///     review because the code looks fine.</item>
///   <item><b>It throws on empty data.</b> Every visualizer starts with no data, so this is the first
///     frame, every time.</item>
///   <item><b>It emits NaN coordinates.</b> A flat series, a single point, a zero-width panel — the
///     arithmetic divides by a span that is zero and the host gets garbage.</item>
///   <item><b>It draws nothing when handed <c>new()</c> options.</b> The record-struct trap: <c>new()</c>
///     binds to the implicit parameterless constructor, every field lands on zero, and a zero-width,
///     fully-transparent widget is indistinguishable from a broken one.</item>
/// </list>
///
/// <para>Driven by a table rather than written per widget, so a widget added later is covered by
/// adding one line — and a contributor who forgets is caught by
/// <see cref="EveryWidgetInTheLibraryIsCovered"/>, which reflects over the assembly.</para>
/// </summary>
public sealed class WidgetLibraryTests
{
    private static RecordingRenderSurface Surface(double width = 400d, double height = 240d) =>
        new(new RenderViewport(width, height, 1d));

    private static IReadOnlyList<double> Values(int count = 24)
    {
        var values = new double[count];
        for (var index = 0; index < count; index++) values[index] = 100d + Math.Sin(index / 3d) * 5d;
        return values;
    }

    private static DepthSnapshot Depth() => new(
        DateTime.UtcNow,
        [new DepthLevel(99.5d, 40), new DepthLevel(99.4d, 90), new DepthLevel(99.3d, 20)],
        [new DepthLevel(99.6d, 30), new DepthLevel(99.7d, 55), new DepthLevel(99.8d, 120)]);

    private static IReadOnlyList<TradePrint> Prints(int count = 6)
    {
        var prints = new TradePrint[count];
        for (var index = 0; index < count; index++)
        {
            prints[index] = new TradePrint(
                new InstrumentId(1),
                DateTime.UnixEpoch.AddSeconds(index), DateTime.UnixEpoch.AddSeconds(index),
                Price: 99.5d + (index * 0.01d), Size: 10 + index,
                index % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell,
                BrokerKind.Simulated, Sequence: index, EventTimeApproximate: false);
        }

        return prints;
    }

    private static IReadOnlyList<ProfileRow> Profile() =>
    [
        new(99.0d, 10d, 8d), new(99.1d, 30d, 22d), new(99.2d, 90d, 70d),
        new(99.3d, 40d, 44d), new(99.4d, 12d, 9d),
    ];

    /// <summary>One widget, in the two shapes every test below needs: drawn with sane options, and
    /// drawn with the all-zero <c>new()</c> options record.</summary>
    public sealed record Widget(
        string Name,
        Action<IRenderSurface> Populated,
        Action<IRenderSurface> Zeroed,
        Action<IRenderSurface> Empty);

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var widget in All) data.Add(widget.Name);
        return data;
    }

    private static Widget Find(string name) => All.Single(w => w.Name == name);

    private static readonly IReadOnlyList<Widget> All =
    [
        new("Series",
            s => Series.Draw(s, "v", Values()),
            s => Series.Draw(s, "v", Values(), new SeriesOptions()),
            s => Series.Draw(s, "v", [])),

        new("Series.Chart",
            s => Series.Chart(s, [SeriesData.Line("a", Values()), SeriesData.Steps("b", Values(12))]),
            s => Series.Chart(s, [new SeriesData("a", Values(), new SeriesOptions())]),
            s => Series.Chart(s, [])),

        new("Histogram",
            s => Histogram.Draw(s, [1d, -2d, 3d, -4d, 5d]),
            s => Histogram.Draw(s, [1d, -2d, 3d], new HistogramOptions()),
            s => Histogram.Draw(s, [])),

        new("Bands",
            s => Bands.Draw(s, Values(), Values(24), Values(24)),
            s => Bands.Draw(s, Values(), Values(24), null, new BandOptions()),
            s => Bands.Draw(s, [], [])),

        new("Signals",
            s => Signals.Draw(s, [new Signal(2, 100d, SignalKind.Buy, "in")], 24, new PlotRange(90d, 110d)),
            s => Signals.Draw(s, [new Signal(2, 100d, SignalKind.Sell)], 24, new PlotRange(90d, 110d), new SignalOptions()),
            s => Signals.Draw(s, [], 24, new PlotRange(90d, 110d))),

        new("Levels",
            s => Levels.Draw(s, [new Level(100d, "vwap")], new PlotRange(90d, 110d)),
            s => Levels.Draw(s, [new Level(100d)], new PlotRange(90d, 110d)),
            s => Levels.Draw(s, [], new PlotRange(90d, 110d))),

        new("Zones",
            s => Zones.Draw(s, 30d, 70d, new PlotRange(0d, 100d)),
            s => Zones.Draw(s, 30d, 70d, new PlotRange(0d, 100d), new ZoneOptions()),
            s => Zones.Draw(s, double.NaN, double.NaN, new PlotRange(0d, 100d))),

        new("Legend",
            s => Legend.Draw(s, [SeriesData.Line("alpha", Values())]),
            s => Legend.Draw(s, [new SeriesData("alpha", Values(), new SeriesOptions())]),
            s => Legend.Draw(s, System.Array.Empty<SeriesData>())),

        new("VolumeProfile",
            s => VolumeProfile.Draw(s, Profile()),
            s => VolumeProfile.Draw(s, Profile(), default, new ProfileOptions()),
            s => VolumeProfile.Draw(s, [])),

        new("DepthCurve",
            s => DepthCurve.Draw(s, Depth()),
            s => DepthCurve.Draw(s, Depth(), new DepthCurveOptions()),
            s => DepthCurve.Draw(s, null)),

        new("Heatmap",
            s => Heatmap.Draw(s, 4, 3, (c, r) => c - r),
            s => Heatmap.Draw(s, 4, 3, (c, r) => c - r, new HeatmapOptions()),
            s => Heatmap.Draw(s, 0, 0, (_, _) => 0d)),

        new("Tiles",
            s => Tiles.Draw(s, [new Tile("PnL", "+120.50"), Tile.Signed("Delta", -4d, "-4")]),
            s => Tiles.Draw(s, [new Tile("PnL", "+120.50")], new TileOptions()),
            s => Tiles.Draw(s, [])),

        new("Gauge",
            s => Gauge.Draw(s, 0.4d, GaugeOptions.Default with { Label = "Imbalance" }),
            s => Gauge.Draw(s, 0.4d, new GaugeOptions()),
            s => Gauge.Draw(s, double.NaN)),

        new("Table",
            s => Table.Draw(s, [new TableColumn("Sym"), TableColumn.Number("Qty")], [["ES", "3"], ["NQ", "1"]]),
            s => Table.Draw(s, [new TableColumn("Sym")], [["ES"]], new TableOptions()),
            s => Table.Draw(s, [], [])),

        new("Tape",
            s => Tape.Draw(s, Prints()),
            s => Tape.Draw(s, Prints(), new TapeOptions()),
            s => Tape.Draw(s, [])),

        new("Equity",
            s => Equity.Draw(s, [100d, 104d, 101d, 108d, 96d, 112d]),
            s => Equity.Draw(s, [100d, 104d, 101d], new EquityOptions()),
            s => Equity.Draw(s, [])),

        new("Candles",
            s => Candles.Draw(s, Bars()),
            s => Candles.Draw(s, Bars(), new CandleOptions()),
            s => Candles.Draw(s, [])),

        new("Footprint",
            s => Footprint.Draw(s, [FootprintFixture()]),
            s => Footprint.Draw(s, [FootprintFixture()], new FootprintOptions()),
            s => Footprint.Draw(s, [])),

        new("Ladder",
            s => Ladder.Draw(s, Depth()),
            s => Ladder.Draw(s, Depth(), new LadderOptions()),
            s => Ladder.Draw(s, null)),
    ];

    private static IReadOnlyList<OhlcvBar> Bars(int count = 12)
    {
        var bars = new OhlcvBar[count];
        for (var index = 0; index < count; index++)
        {
            var open = 100d + Math.Sin(index / 2d);
            bars[index] = new OhlcvBar(
                new InstrumentId(1), BarSize.OneMinute, DateTime.UnixEpoch.AddMinutes(index),
                open, open + 1d, open - 1d, open + 0.5d, 1000L, BrokerKind.Simulated, true);
        }

        return bars;
    }

    private static FootprintBar FootprintFixture()
    {
        FootprintFeatureRow[] rows =
        [
            new(99.0d, 20L, 14L, false, false, false, false),
            new(99.1d, 55L, 30L, false, false, false, false),
            new(99.2d, 12L, 41L, true, false, false, false),
        ];

        return new FootprintBar(
            DateTime.UnixEpoch, DateTime.UnixEpoch.AddMinutes(1), rows,
            PocPrice: 99.1d, VolumeCentroid: 99.1d, BuyCentroid: 99.1d, SellCentroid: 99.1d,
            BuyVolume: 87L, SellVolume: 85L, Delta: 2L, CumulativeDelta: 2L,
            StackedBuy: 0, StackedSell: 0, Quality: FeedQuality.RealTape);
    }

    // ── the four properties ─────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Names))]
    public void ItDrawsSomething(string name)
    {
        var surface = Surface();
        Find(name).Populated(surface);

        Assert.False(surface.IsBlank, $"{name} drew nothing at all, which reads as a broken host");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EmptyDataIsTheFirstFrameAndMustNotThrow(string name)
    {
        var surface = Surface();

        // No assertion on what it drew: some widgets legitimately draw nothing when they have nothing.
        // What none of them may do is throw, because this is the state every visualizer starts in.
        Find(name).Empty(surface);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void NoWidgetEmitsANonFiniteCoordinate(string name)
    {
        var surface = Surface();
        Find(name).Populated(surface);

        Assert.False(surface.HasNonFiniteCoordinate, $"{name} emitted NaN or infinity to the host");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ZeroedOptionsStillDraw(string name)
    {
        // The record-struct trap, pinned for every widget at once: `new()` skips the primary
        // constructor's defaults, so an unguarded widget handed one draws a zero-width, fully
        // transparent nothing.
        var surface = Surface();
        Find(name).Zeroed(surface);

        Assert.False(surface.IsBlank, $"{name} handed new() options drew nothing — it needs a Default fallback");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ATinyPanelIsSurvivedRatherThanDrawnPast(string name)
    {
        // Panels are user-resizable and start at whatever the layout gives them. A widget that assumes
        // room throws or scribbles outside its area on the first frame after a drag.
        var surface = Surface(width: 3d, height: 2d);
        Find(name).Populated(surface);

        Assert.False(surface.HasNonFiniteCoordinate);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AFlatSeriesDoesNotCollapseTheArithmetic(string name)
    {
        // A zero-span range is a division by zero waiting to happen, and it is a completely ordinary
        // input: an unchanged price, a position held at one size, a book with one level.
        var surface = Surface();
        var flat = new[] { 100d, 100d, 100d, 100d };

        switch (name)
        {
            case "Series": Series.Draw(surface, "v", flat); break;
            case "Histogram": Histogram.Draw(surface, flat); break;
            case "Bands": Bands.Draw(surface, flat, flat); break;
            case "Equity": Equity.Draw(surface, flat); break;
            default: return;
        }

        Assert.False(surface.HasNonFiniteCoordinate);
        Assert.False(surface.IsBlank, $"{name} drew nothing for a flat series");
    }

    [Fact]
    public void EveryWidgetInTheLibraryIsCovered()
    {
        // The table above is only worth having if it cannot fall behind. Anything in DaxAlgo.Sdk.Drawing
        // exposing a public static Draw is a widget, and must appear in it.
        var drawable = typeof(Series).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == "DaxAlgo.Sdk.Drawing" && t.IsAbstract && t.IsSealed)
            .Where(t => t.GetMethods().Any(m => m.IsStatic && m.IsPublic && m.Name == "Draw"))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var covered = All.Select(w => w.Name.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        drawable.ExceptWith(covered);

        Assert.True(drawable.Count == 0, $"not covered by WidgetLibraryTests: {string.Join(", ", drawable)}");
    }
}
