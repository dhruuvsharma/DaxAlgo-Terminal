using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// The order book as a landscape: resting size across price, receding into the past, drawn in three
/// dimensions with nothing but lines.
///
/// <para><b>This is the exemplar for two things the library cannot do for you.</b> The first is 3D —
/// there is no 3D surface and there will not be one, because a unit never touches a control. What
/// there is instead is arithmetic: <see cref="Projection3"/> turns a world point into a panel point,
/// the unit sorts by depth and draws far to near, and the host renders ordinary 2D primitives without
/// knowing anything happened. The second is <b>composing a picture by hand</b>. Every other exemplar
/// calls a widget — <c>Ladder.Draw</c>, <c>Heatmap.Draw</c> — and a brief that asks for a picture the
/// library has never seen needs a worked example of building one out of primitives instead.</para>
///
/// <para><b>Painter's algorithm, and it is exact here.</b> Rows are walked oldest first, so nearer
/// rows are drawn last and cover the ones behind them. That is correct for a height field and for
/// scattered markers; it sorts wrongly for shapes that interpenetrate, and no ordering fixes that.</para>
///
/// <para>The camera turns with <c>surface.Now</c>, which is the whole animation: a frame is a function
/// of the clock, nothing is accumulated while drawing, and <c>Draw</c> stays pure.</para>
/// </summary>
public sealed class DepthLandscapeVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string RowsParameter = "rows";
    public const string HistoryParameter = "history";
    public const string SpinParameter = "spin";

    /// <summary>Price steps each side of the mid. The landscape is this wide either way.</summary>
    private int _halfWidth;

    private int _history;
    private bool _spin;
    private InstrumentId _instrument;

    /// <summary>Inferred from the book, because the host does not publish a tick size.</summary>
    private double _tick;

    private DateTime _startedAt;

    /// <summary>One captured slice: resting size per price step, bid side negative-indexed.</summary>
    private readonly List<double[]> _rows = [];

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Int(RowsParameter, "Price steps each side", 14, min: 4, max: 40,
            group: "Landscape", unit: "ticks"),
        StrategyParameter.Int(HistoryParameter, "History", 48, min: 8, max: 120,
            group: "Landscape", unit: "slices"),
        StrategyParameter.Bool(SpinParameter, "Turn the camera", true, group: "Landscape"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Depth;

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _halfWidth = context.Parameters.GetInt(RowsParameter);
        _history = context.Parameters.GetInt(HistoryParameter);
        _spin = context.Parameters.GetBool(SpinParameter);

        // Stamped from the host clock, so the age read in Draw is a difference between two readings of
        // ONE clock. A render-side stopwatch would look identical here and be meaningless.
        _startedAt = context.Clock.UtcNow;

        _rows.Clear();
        _tick = 0d;
        return Task.CompletedTask;
    }

    public Task OnDepthAsync(
        InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ct.ThrowIfCancellationRequested();

        if (instrument != _instrument) return Task.CompletedTask;
        if (!double.IsFinite(depth.BestBid) || !double.IsFinite(depth.BestAsk)) return Task.CompletedTask;

        LearnTick(depth);
        if (_tick <= 0d) return Task.CompletedTask;

        var mid = (depth.BestBid + depth.BestAsk) / 2d;
        var row = new double[(2 * _halfWidth) + 1];

        Fill(row, depth.Bids, mid);
        Fill(row, depth.Asks, mid);

        // Bounded, because a window stays open for hours and this is the only thing that grows.
        if (_rows.Count == _history) _rows.RemoveAt(0);
        _rows.Add(row);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The whole frame, and it opens its panel here.
    ///
    /// <para><b>Deliberately not split into a one-line wrapper over a private drawing method</b>, which
    /// is how the other exemplars are written. Three generated units in a row copied the inner method
    /// and dropped the <c>surface.Panel</c> scope that was sitting alone in the wrapper — primitives
    /// outside any panel, which the ladder reports as <c>draw.no-panel</c>. A model copies the shape it
    /// is shown, so the scope has to be in the same method as the work it guards.</para>
    /// </summary>
    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        // EVERY frame starts here. Nothing is drawn outside a panel.
        using var panel = surface.Panel("Depth landscape", RenderPanelKind.Canvas);
        var area = PlotArea.Of(surface);

        if (_rows.Count < 2) { Plot.Waiting(surface, "Building the landscape from the book…"); return; }

        // A collapsed panel reports zero size, and every coordinate scaled by it would be non-finite.
        // Returning early is the whole guard; the host draws the empty frame it asked for.
        if (area.Width < 1d || area.Height < 1d) return;

        var tallest = Tallest();
        if (tallest <= 0d) { Plot.Waiting(surface, "The book is empty."); return; }

        // The animation, and all of it: an angle derived from the clock. Nothing is incremented here,
        // because Draw runs more than once per frame and an incremented angle would turn at double
        // speed and stutter.
        var camera = Camera3.Default;
        if (_spin)
        {
            var seconds = (surface.Now - _startedAt).TotalSeconds;
            if (double.IsFinite(seconds) && seconds > 0d) camera = camera.Orbit(seconds * 0.12d);
        }

        var projection = Projection3.Of(camera, area.Width, area.Height);

        // Reset per frame, because Draw is pure and runs more than once per frame — a nearest-point
        // search that carried over would keep a stale pick from the discovery pass.
        _hover = (double.MaxValue, 0d, 0d, 0, 0d);

        // Oldest first, so nearer rows are drawn last and cover what is behind them.
        for (var r = 0; r < _rows.Count; r++)
        {
            var z = 1d - (2d * r / (_rows.Count - 1));
            DrawRow(surface, area, projection, _rows[r], z, tallest);
        }

        DrawHover(surface);
    }

    /// <summary>One slice of the book as a polyline across price, at its own distance into the past.</summary>
    /// <summary>
    /// The point nearest the pointer, in screen space, and what it holds. Reset each frame.
    ///
    /// <para><b>A crosshair does not translate to a projected scene</b> — there is no axis to drop a
    /// line onto — but the thing a crosshair is FOR does: telling the viewer what they are looking at.
    /// Since every vertex is already projected to draw it, the nearest one is a comparison rather than
    /// an inverse projection, which the SDK deliberately does not provide.</para>
    /// </summary>
    private (double Distance, double X, double Y, int Step, double Size) _hover;

    private void DrawRow(
        IRenderSurface surface, PlotArea area, Projection3 projection, double[] row, double z, double tallest)
    {
        // Older rows fade, which is what gives a wireframe its depth without any shading model.
        var age = (z + 1d) / 2d;
        surface.SetStyle(new RenderStyle(
            surface.Theme(RenderThemeColor.Accent), Thickness: 1d, Alpha: 0.25d + (0.75d * (1d - age))));

        var previous = default(Projected);
        var havePrevious = false;

        for (var i = 0; i < row.Length; i++)
        {
            var x = -1d + (2d * i / (row.Length - 1));
            var y = Num.SafeDiv(row[i], tallest) * 0.55d;

            var point = projection.Project(new Vec3(x, y, z));

            // A point behind the camera projects somewhere entirely plausible, so the segment touching
            // it has to be dropped rather than drawn.
            if (point.InFront && havePrevious)
                surface.Line(area.X + previous.X, area.Y + previous.Y, area.X + point.X, area.Y + point.Y);

            if (point.InFront) TrackHover(surface, area, point, i, row[i]);

            previous = point;
            havePrevious = point.InFront;
        }
    }

    /// <summary>Keeps the projected vertex closest to the pointer. A read of <c>Cursor</c>, never a
    /// handler — the host accumulates the gesture and this only looks at where it ended up.</summary>
    private void TrackHover(IRenderSurface surface, PlotArea area, Projected point, int step, double size)
    {
        var cursor = surface.Cursor;
        if (!cursor.IsInside) return;

        var dx = area.X + point.X - cursor.X;
        var dy = area.Y + point.Y - cursor.Y;
        var distance = (dx * dx) + (dy * dy);

        if (distance < _hover.Distance)
            _hover = (distance, area.X + point.X, area.Y + point.Y, step - _halfWidth, size);
    }

    /// <summary>Marks what the pointer is nearest and says what it holds.</summary>
    private void DrawHover(IRenderSurface surface)
    {
        // 24 pixels: near enough to mean "that one", far enough to survive a hand that is not steady.
        if (_hover.Distance > 24d * 24d) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), Thickness: 1.5d));
        surface.Marker(_hover.X, _hover.Y, RenderMarkerShape.Circle);
        surface.Text(
            _hover.X + 8d,
            _hover.Y - 6d,
            $"{_hover.Step:+0;-0;0} ticks   {_hover.Size:N0}");
    }

    private double Tallest()
    {
        var tallest = 0d;
        foreach (var row in _rows)
        {
            foreach (var size in row)
            {
                if (size > tallest) tallest = size;
            }
        }
        return tallest;
    }

    private void Fill(double[] row, IReadOnlyList<DepthLevel> side, double mid)
    {
        foreach (var level in side)
        {
            if (!double.IsFinite(level.Price) || !double.IsFinite(level.Size)) continue;

            var step = (int)Math.Round((level.Price - mid) / _tick) + _halfWidth;
            if (step >= 0 && step < row.Length) row[step] += level.Size;
        }
    }

    /// <summary>The smallest gap between adjacent levels on one side — the book's own step, which the
    /// host does not publish. Learned once and kept, so a thin moment cannot widen it.</summary>
    private void LearnTick(DepthSnapshot depth)
    {
        if (_tick > 0d) return;

        var smallest = double.MaxValue;
        foreach (var side in new[] { depth.Bids, depth.Asks })
        {
            for (var i = 1; i < side.Count; i++)
            {
                var gap = Math.Abs(side[i].Price - side[i - 1].Price);
                if (gap > 0d && double.IsFinite(gap) && gap < smallest) smallest = gap;
            }
        }

        if (smallest < double.MaxValue) _tick = smallest;
    }
}
