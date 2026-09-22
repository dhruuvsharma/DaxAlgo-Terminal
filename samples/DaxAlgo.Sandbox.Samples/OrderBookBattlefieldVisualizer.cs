using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// The order book as a 3D battlefield (Hyperion / NewHedge style): each resting size chunk is a
/// soldier on its price; bulls (bids) face bears (asks) across the mid front line; large trades
/// strike toward the line. Built with <see cref="Projection3"/> — no 3D host control.
/// </summary>
public sealed class OrderBookBattlefieldVisualizer : IVisualizer
{
    public const string InstrumentParameter = "instrument";
    public const string HalfWidthParameter = "halfWidth";
    public const string TroopUnitParameter = "troopUnit";
    public const string SpinParameter = "spin";

    private InstrumentId _instrument;
    private int _halfWidth;
    private double _troopUnit;
    private bool _spin;
    private double _tick;
    private DateTime _startedAt;
    private DepthSnapshot? _depth;
    private readonly List<(double Price, long Size, bool IsBuy, DateTime Utc)> _strikes = [];

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Int(HalfWidthParameter, "Price steps each side", 20, min: 6, max: 40,
            group: "Battlefield", unit: "ticks"),
        StrategyParameter.Number(TroopUnitParameter, "Size per soldier", 5d, min: 1d, max: 500d,
            group: "Battlefield"),
        StrategyParameter.Bool(SpinParameter, "Turn the camera", true, group: "Battlefield"));

    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

    public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _halfWidth = context.Parameters.GetInt(HalfWidthParameter);
        _troopUnit = context.Parameters.GetDouble(TroopUnitParameter);
        _spin = context.Parameters.GetBool(SpinParameter);
        _startedAt = context.Clock.UtcNow;
        _tick = 0d;
        _depth = null;
        _strikes.Clear();
        return Task.CompletedTask;
    }

    public Task OnDepthAsync(
        InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depth);
        if (instrument != _instrument) return Task.CompletedTask;
        LearnTick(depth);
        _depth = depth;
        return Task.CompletedTask;
    }

    public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trade);
        var isBuy = trade.Aggressor == AggressorSide.Buy;
        _strikes.Add((trade.Price, trade.Size, isBuy, trade.EventTimeUtc));
        while (_strikes.Count > 48) _strikes.RemoveAt(0);
        return Task.CompletedTask;
    }

    public void Draw(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        using var panel = surface.Panel("Battlefield", RenderPanelKind.Canvas);
        var area = PlotArea.Of(surface);
        if (area.Width < 1d || area.Height < 1d) return;

        var depth = _depth;
        if (depth is null || _tick <= 0d)
        {
            Plot.Waiting(surface, "Forming the armies from the book…");
            return;
        }

        var mid = (depth.BestBid + depth.BestAsk) * 0.5;
        var soldiers = BattlefieldForces.FromDepth(depth, _tick, _halfWidth, _troopUnit);
        if (soldiers.Count == 0)
        {
            Plot.Waiting(surface, "The book is empty.");
            return;
        }

        var camera = Camera3.Framing(Corners);
        if (_spin)
        {
            var seconds = (surface.Now - _startedAt).TotalSeconds;
            if (double.IsFinite(seconds) && seconds > 0d)
                camera = camera.Orbit(seconds * 0.15d);
        }

        var projection = Projection3.Of(camera, area.Width, area.Height);

        DrawGround(surface, area, projection);
        DrawFrontLine(surface, area, projection);

        // Painter's algorithm: far first.
        var drawn = new List<(double Depth, double X, double Y, bool Bid, double Size)>(soldiers.Count);
        foreach (var s in soldiers)
        {
            var p = projection.Project(new Vec3(s.X, s.Y, s.Z));
            if (!p.InFront) continue;
            drawn.Add((p.Depth, area.X + p.X, area.Y + p.Y, s.IsBid, s.Size));
        }
        drawn.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        foreach (var s in drawn)
        {
            surface.SetStyle(new RenderStyle(
                surface.Theme(s.Bid ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
                Thickness: 1.4d,
                Alpha: 0.9d));
            surface.Marker(s.X, s.Y, s.Bid ? RenderMarkerShape.Triangle : RenderMarkerShape.Diamond);
        }

        DrawStrikes(surface, area, projection, mid);
        DrawHud(surface, area, soldiers, depth);

        // Nearest soldier under the pointer — same hover contract as the landscape exemplar.
        var cursor = surface.Cursor;
        if (cursor.IsInside && drawn.Count > 0)
        {
            var nearest = drawn[0];
            var best = double.MaxValue;
            foreach (var s in drawn)
            {
                var dx = s.X - cursor.X;
                var dy = s.Y - cursor.Y;
                var d2 = dx * dx + dy * dy;
                if (d2 >= best) continue;
                best = d2;
                nearest = s;
            }
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 10d));
            surface.Text(area.X + 8d, area.Y + area.Height - 12d,
                $"{(nearest.Bid ? "Bid" : "Ask")} troop  size {nearest.Size:0}");
        }
    }

    private void DrawStrikes(IRenderSurface surface, PlotArea area, Projection3 projection, double mid)
    {
        var now = surface.Now;
        var recent = _strikes.Where(s => (now - s.Utc).TotalSeconds < 4).Select(s =>
            (s.Price, s.Size, s.IsBuy));
        var strikes = BattlefieldForces.FromTrades(recent, mid, _tick, _halfWidth, minSize: (long)_troopUnit);
        foreach (var strike in strikes)
        {
            var a = projection.Project(new Vec3(strike.X0, strike.Y0, strike.Z0));
            var b = projection.Project(new Vec3(strike.X1, strike.Y1, strike.Z1));
            if (!a.InFront || !b.InFront) continue;
            surface.SetStyle(new RenderStyle(
                surface.Theme(strike.IsBuy ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
                Thickness: 2d,
                Alpha: 0.75d));
            surface.Line(area.X + a.X, area.Y + a.Y, area.X + b.X, area.Y + b.Y);
        }
    }

    private static void DrawGround(IRenderSurface surface, PlotArea area, Projection3 projection)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Border), Thickness: 1d, Alpha: 0.35d));
        Projected? prev = null;
        for (var i = 0; i <= 20; i++)
        {
            var x = -1d + (2d * i / 20d);
            var p = projection.Project(new Vec3(x, 0d, 0d));
            if (p.InFront && prev is { InFront: true } q)
                surface.Line(area.X + q.X, area.Y + q.Y, area.X + p.X, area.Y + p.Y);
            prev = p;
        }
    }

    private static void DrawFrontLine(IRenderSurface surface, PlotArea area, Projection3 projection)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Warning), Thickness: 2d, Alpha: 0.9d));
        var a = projection.Project(new Vec3(0d, 0d, -0.35d));
        var b = projection.Project(new Vec3(0d, 0.55d, 0.35d));
        if (a.InFront && b.InFront)
            surface.Line(area.X + a.X, area.Y + a.Y, area.X + b.X, area.Y + b.Y);
    }

    private static void DrawHud(
        IRenderSurface surface, PlotArea area, IReadOnlyList<BattlefieldForces.Soldier> soldiers, DepthSnapshot depth)
    {
        var bulls = soldiers.Count(s => s.IsBid);
        var bears = soldiers.Count(s => !s.IsBid);
        var total = Math.Max(1, bulls + bears);
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), Thickness: 1d));
        surface.Text(area.X + 8d, area.Y + 14d,
            $"BULLS {bulls} ({bulls / (double)total:P0})   |   BEARS {bears} ({bears / (double)total:P0})");
        surface.Text(area.X + 8d, area.Y + 30d,
            $"front {(depth.BestBid + depth.BestAsk) * 0.5:0.####}   spread {depth.BestAsk - depth.BestBid:0.####}");
    }

    private static Vec3[] Corners { get; } =
    [
        new(-1.05d, 0d, -0.5d), new(1.05d, 0d, -0.5d), new(-1.05d, 0d, 0.5d), new(1.05d, 0d, 0.5d),
        new(-1.05d, 0.6d, -0.5d), new(1.05d, 0.6d, -0.5d), new(-1.05d, 0.6d, 0.5d), new(1.05d, 0.6d, 0.5d),
    ];

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
