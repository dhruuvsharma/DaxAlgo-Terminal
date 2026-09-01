using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace DaxAlgo.Sandbox.Samples;

/// <summary>
/// Several indicators, each read at several lookbacks, laid out as a matrix of verdicts.
///
/// <para><b>The strategy exemplar for composing a picture.</b> The other kernel sample answers its
/// brief with <c>Series.Chart</c> and a tile strip — the right answer for a line, and the reason
/// generated strategies all came out as a chart with numbers under it. Every strategy brief was shown
/// that one worked example, so every strategy copied its shape. Nothing below is a widget except the
/// readout: the matrix is <c>Rect</c> and <c>Text</c>.</para>
///
/// <para><b>A matrix is the shape a regime screen actually is</b> — one row per thing measured, one
/// column per horizon, the cell coloured by the verdict, and the rows SORTED so the agreement is
/// visible without reading. Widen it to one row per index constituent and it is the same drawing
/// code; the axis is a list of names either way.</para>
///
/// <para>The skeleton is the one the hand-written windows use, and it is worth following in order:
/// guard, choose the visible window, derive the axis from the data, take one global scale, build the
/// mappers once, then lay the passes down — grid, cells, composite, and the hover readout last so it
/// survives everything under it.</para>
/// </summary>
public sealed class RegimeMatrixKernel : IStrategyKernel
{
    public const string InstrumentParameter = "instrument";
    public const string EntryParameter = "entry";
    public const string SizeParameter = "size";

    /// <summary>The horizons every indicator is read at. Powers of roughly two, so each column is a
    /// genuinely different regime rather than the same one resampled.</summary>
    private static readonly int[] Horizons = [7, 14, 28, 56];

    private static readonly string[] Measures = ["Momentum", "RSI", "MACD", "Trend", "Volatility"];

    private const int Warmup = 60;

    private InstrumentId _instrument;
    private double _entry;
    private double _size;

    private readonly List<double> _closes = [];

    /// <summary>The grid, measures down and horizons across, each in [-1, +1]. Rebuilt on the close of
    /// a bar rather than in <c>Draw</c>: drawing runs more than once a frame, and recomputing there
    /// would do the work twice and let the two frames disagree.</summary>
    private readonly double[,] _verdicts = new double[Measures.Length, Horizons.Length];

    private double _composite;
    private int _bars;

    public StrategyParameterSchema Schema { get; } = new(
        StrategyParameter.Instrument(
            InstrumentParameter, "Instrument", new InstrumentId(1), group: "Market"),
        StrategyParameter.Number(
            EntryParameter, "Entry threshold", 0.35d, min: 0.05d, max: 0.95d, group: "Signal"),
        StrategyParameter.Number(
            SizeParameter, "Position size", 1d, min: 0.01d, max: 1000d, group: "Signal", unit: "units"));

    public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

    public UnitLayout Layout => UnitLayout.Rows(
        UnitLayout.Panel("Regime matrix", DrawMatrix).Star(4),
        UnitLayout.Panel("Composite", DrawReadout).Pixels(64));

    public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        _instrument = context.Parameters.GetInstrument(InstrumentParameter);
        _entry = context.Parameters.GetDouble(EntryParameter);
        _size = context.Parameters.GetDouble(SizeParameter);
        return Task.CompletedTask;
    }

    public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(context);
        if (bar.InstrumentId != _instrument || !bar.IsFinal) return Task.CompletedTask;

        _closes.Add(bar.Close);
        if (_closes.Count > 512) _closes.RemoveAt(0);
        _bars++;

        if (_closes.Count < Warmup) return Task.CompletedTask;

        Score();

        // One number out of the grid, and it is a mean rather than a sum so the threshold means the
        // same thing whatever the matrix is sized at. A sum would make "0.35" stricter every time a
        // row is added, which is the sort of coupling nobody notices until the strategy stops trading.
        var total = 0d;
        foreach (var v in _verdicts) total += v;
        _composite = total / (Measures.Length * Horizons.Length);

        var target = _composite >= _entry ? _size
            : _composite <= -_entry ? -_size
            : 0d;

        context.Book.SetTargetPosition(_instrument, target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fills the grid. Every cell is squashed into [-1, +1] so one colour scale reads across the whole
    /// matrix — a row in raw units would dominate the picture for reasons of scale rather than signal.
    /// </summary>
    private void Score()
    {
        for (var h = 0; h < Horizons.Length; h++)
        {
            var n = Horizons[h];

            _verdicts[0, h] = Num.Clamp(Momentum(n) * 8d, -1d, 1d);
            _verdicts[1, h] = Num.Clamp((RsiOver(n) - 50d) / 25d, -1d, 1d);
            _verdicts[2, h] = Num.Clamp(MacdOver(n) * 40d, -1d, 1d);
            _verdicts[3, h] = Num.Clamp(Trend(n) * 12d, -1d, 1d);

            // Volatility is signed by DIRECTION, because "volatile" on its own is not bullish or
            // bearish and a colour scale has to mean one thing. Rising vol against a falling price is
            // the bearish half; the same vol with price up is the bullish one.
            _verdicts[4, h] = Num.Clamp(-Volatility(n) * 20d * Math.Sign(Momentum(n)), -1d, 1d);
        }
    }

    private double Momentum(int n)
    {
        if (_closes.Count <= n) return 0d;
        var then = _closes[^(n + 1)];
        return then <= 0d ? 0d : (_closes[^1] - then) / then;
    }

    private double RsiOver(int n)
    {
        var rsi = new Rsi(n);
        foreach (var close in _closes) rsi.Update(close);
        return rsi.IsReady ? rsi.Value : 50d;
    }

    private double MacdOver(int n)
    {
        var macd = new Macd(Math.Max(2, n / 2), n, Math.Max(2, n / 3));
        foreach (var close in _closes) macd.Update(close);
        var last = _closes[^1];
        return !macd.IsReady || last <= 0d ? 0d : macd.Value / last;
    }

    private double Trend(int n)
    {
        var fast = new Sma(Math.Max(2, n / 2));
        var slow = new Sma(n);
        foreach (var close in _closes) { fast.Update(close); slow.Update(close); }
        return !fast.IsReady || !slow.IsReady || slow.Value <= 0d ? 0d : (fast.Value - slow.Value) / slow.Value;
    }

    private double Volatility(int n)
    {
        var vol = new RealizedVolatility(n);
        foreach (var close in _closes) vol.Update(close);
        return vol.IsReady ? vol.Value : 0d;
    }

    // ── the picture ─────────────────────────────────────────────────────────────────────────────

    private void DrawMatrix(IRenderSurface surface)
    {
        var area = PlotArea.Of(surface);

        const double labelWidth = 92d;
        const double headerHeight = 18d;

        if (area.Width <= labelWidth + 40d || area.Height <= headerHeight + 20d) return;
        if (_closes.Count < Warmup)
        {
            Plot.Waiting(surface, $"Warming up — {_closes.Count} of {Warmup} bars.");
            return;
        }

        // The rows, ORDERED. Strongest agreement first, because the point of a matrix is to see which
        // measures agree without reading every cell — unsorted, that is exactly the work it saves.
        var order = Enumerable.Range(0, Measures.Length)
            .OrderByDescending(RowStrength)
            .ToArray();

        var cellW = (area.Width - labelWidth) / Horizons.Length;
        var cellH = (area.Height - headerHeight) / Measures.Length;

        double X(int column) => area.X + labelWidth + column * cellW;
        double Y(int row) => area.Y + headerHeight + row * cellH;

        DrawHeader(surface, area, labelWidth, headerHeight, cellW);

        for (var r = 0; r < order.Length; r++)
        {
            var measure = order[r];

            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 10d));
            surface.Text(area.X + 4d, Y(r) + cellH * 0.5d - 6d, Measures[measure]);

            for (var h = 0; h < Horizons.Length; h++) DrawCell(surface, _verdicts[measure, h], X(h), Y(r), cellW, cellH);
        }

        DrawHover(surface, area, order, labelWidth, headerHeight, cellW, cellH);
    }

    /// <summary>The horizon each column is, written once at the top. A grid whose axes are unlabelled
    /// is a decoration — the reader cannot tell the 7-bar column from the 56-bar one.</summary>
    private static void DrawHeader(
        IRenderSurface surface, PlotArea area, double labelWidth, double headerHeight, double cellW)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary), FontSize: 9.5d));
        for (var h = 0; h < Horizons.Length; h++)
        {
            surface.Text(
                area.X + labelWidth + h * cellW + cellW * 0.5d - 10d, area.Y + 3d,
                Horizons[h].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Grid), Thickness: 1d));
        surface.Line(area.X, area.Y + headerHeight, area.Right, area.Y + headerHeight);
    }

    /// <summary>
    /// One verdict. Colour carries the SIGN and alpha carries the strength, so a wall of weak
    /// agreement never looks like a strong signal — two encodings of one number is what makes a grid
    /// readable at a glance rather than at a squint.
    /// </summary>
    private static void DrawCell(
        IRenderSurface surface, double verdict, double x, double y, double width, double height)
    {
        if (width <= 1d || height <= 1d) return;

        var strength = Math.Clamp(Math.Abs(verdict), 0d, 1d);
        var tone = verdict > 0.05d ? RenderThemeColor.Bullish
            : verdict < -0.05d ? RenderThemeColor.Bearish
            : RenderThemeColor.Neutral;

        // Floored, so a cell that was computed is visibly a cell. A neutral verdict drawn at zero
        // alpha leaves a hole the reader mistakes for missing data.
        surface.SetStyle(new RenderStyle(surface.Theme(tone), Alpha: 0.18d + 0.72d * strength));
        surface.Rect(x + 1d, y + 1d, width - 2d, height - 2d);

        if (height < 16d || width < 34d) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Text), FontSize: 9.5d));
        surface.Text(
            x + width * 0.5d - 12d, y + height * 0.5d - 6d,
            verdict.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The row under the pointer, outlined and named. Drawn last so nothing covers it.</summary>
    private void DrawHover(
        IRenderSurface surface, PlotArea area, int[] order,
        double labelWidth, double headerHeight, double cellW, double cellH)
    {
        var cursor = surface.Cursor;
        if (!cursor.IsInside || cursor.Y < area.Y + headerHeight) return;

        var row = (int)((cursor.Y - area.Y - headerHeight) / Math.Max(1e-6d, cellH));
        if (row < 0 || row >= order.Length) return;

        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), Thickness: 1.3d));
        surface.Rect(
            area.X + labelWidth, area.Y + headerHeight + row * cellH,
            cellW * Horizons.Length, cellH, filled: false);

        var measure = order[row];
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent), FontSize: 10d));
        surface.Text(
            area.X + 4d, area.Y + headerHeight + row * cellH + 2d,
            $"{Measures[measure]} {RowStrength(measure):+0.00;-0.00;0.00}");
    }

    private double RowStrength(int measure)
    {
        var total = 0d;
        for (var h = 0; h < Horizons.Length; h++) total += _verdicts[measure, h];
        return total / Horizons.Length;
    }

    private void DrawReadout(IRenderSurface surface)
    {
        var stance = _composite >= _entry ? "LONG" : _composite <= -_entry ? "SHORT" : "FLAT";

        // Agreement is what a matrix is FOR: twelve cells leaning the same way is a different claim
        // from twelve cancelling out to the same mean.
        var agree = 0;
        foreach (var v in _verdicts) if (Math.Sign(v) == Math.Sign(_composite) && Math.Abs(v) > 0.05d) agree++;

        Tiles.Draw(surface,
        [
            new Tile("Composite", _composite.ToString("+0.000;-0.000;0.000",
                    System.Globalization.CultureInfo.InvariantCulture), "mean verdict",
                _composite >= 0d ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
            new Tile("Stance", stance, $"entry ±{_entry:0.00}",
                stance == "FLAT" ? RenderThemeColor.TextSecondary
                    : stance == "LONG" ? RenderThemeColor.Bullish : RenderThemeColor.Bearish),
            new Tile("Agreement", $"{agree} / {Measures.Length * Horizons.Length}", "cells with the composite"),
            new Tile("Bars", _bars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), "seen"),
        ]);
    }
}
