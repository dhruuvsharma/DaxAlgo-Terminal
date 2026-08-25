---
id: drawing
name: Drawing — the widget library, and composing your own
triggers: window, view, ui, panel, chart, display, show, render, dashboard, visual, plot, gui, screen, draw, drawing, candle, candlestick, ladder, footprint, heatmap, graph, picture, paint, depth, profile, tape, equity, gauge, table, tile, band, histogram, signal, level, legend, volume
---

# Drawing

You describe the whole frame every time you are asked, and the host retains nothing between frames.
That suits streaming market data, where most of the picture changes on every tick, and it means you
hold no visual state the host has to reconcile.

**There is no control, no XAML, no window.** `Draw` gets an `IRenderSurface` and nothing else. That is
what lets a stranger's visualizer run in this process at all.

## Reach for a widget before you draw anything by hand

There is a library of them. **Check this table first** — hand-rolling one of these is slower to write,
longer to read, and fails verification in ways the widget already handles (blank first frame, a flat
series that divides by zero, colour used where shape was needed, a scale that flatters the data).

Each takes an options record. **Pass `Default`, or omit the argument.** Never `new()` — on a record
struct that binds to the implicit parameterless constructor, every field lands on zero, and a
zero-width fully-transparent widget looks exactly like a broken one. To change one field:
`SeriesOptions.Default with { Color = RenderThemeColor.Bearish }`.

### On a chart

| Want | Call |
|---|---|
| A line, area, step or scatter series | `Series.Draw(surface, "name", values)` |
| Several series on **one shared scale**, with grid, axes, legend and crosshair | `Series.Chart(surface, [SeriesData.Line("fast", fast), SeriesData.Line("slow", slow)])` |
| OHLC candles | `Candles.Draw(surface, bars)` |
| Signed bars from a baseline — MACD, delta, volume | `Histogram.Draw(surface, values)` |
| A shaded envelope — Bollinger, Keltner, VWAP bands | `Bands.Draw(surface, upper, lower, middle)` |
| Entry/exit/cross markers | `Signals.Draw(surface, signals, count, range)` |
| Labelled reference lines — VWAP, stop, POC, session high | `Levels.Draw(surface, price, "stop", range)` |
| Shaded threshold zones — RSI 30/70 | `Zones.Draw(surface, 30, 70, range)` |
| The series key | `Legend.Draw(surface, series)` |
| Equity curve with drawdown shading | `Equity.Draw(surface, equity)` |

### Order flow and the book

| Want | Call |
|---|---|
| Depth ladder — what rests at each price | `Ladder.Draw(surface, depth)` |
| Depth chart — cumulative size, where the wall is | `DepthCurve.Draw(surface, depth)` |
| Volume footprint — buy/sell split per price per bar | `Footprint.Draw(surface, bars)` |
| Volume at price, with POC and value area | `VolumeProfile.Draw(surface, rows)` |
| Time and sales | `Tape.Draw(surface, prints)` |

### Readouts and dashboards

| Want | Call |
|---|---|
| Numbers stated rather than plotted | `Tiles.Draw(surface, [new Tile("PnL", "+120.50")])` |
| A bounded meter — imbalance, VPIN, confidence | `Gauge.Draw(surface, value)` |
| Rows and columns — positions, orders, fills | `Table.Draw(surface, columns, rows)` |
| A shaded matrix — correlation, liquidity, hour-of-day | `Heatmap.Draw(surface, columns, rows, (c, r) => value)` |
| A value-to-colour ramp | `ColorScale.Diverging(surface, value, extent)` |
| Grid, axes, crosshair, the "waiting" frame | `Plot.*` |

## What the widgets already handle

- **`Plot.Waiting(surface, "…")`** is the empty state in one line, and returns `true`:
  `if (_history.Count == 0) { Plot.Waiting(surface); return; }`. Drawing nothing is the commonest way a
  picture fails review, and a blank panel reads as a broken application.
- **One scale across a comparison.** `Series.Chart` scales every series together; separately-scaled
  series look like they agree when they do not, and that chart looks exactly like a correct one.
- **A histogram's baseline stays in range**, so bars either side of zero point different ways.
- **Both sides of the book share one size scale** in `DepthCurve` — a lopsided book looking balanced is
  the one thing that picture exists to reveal.
- **Shape as well as colour on `Signals`**: buy triangle, sell diamond, exit cross.
- **`VolumeProfile.ValueArea(rows)`** returns the same low/high/POC the picture drew, so a strategy can
  trade the levels its own chart shows.
- **Flat series, single points and tiny panels** are handled. None of them produce NaN.

## Placing widgets: `PlotArea`

Any widget can be given a rectangle instead of filling the panel. That is how a dashboard is built.

```csharp
var area = PlotArea.Of(surface);
var (header, body) = area.SplitTop(56d);          // strip, and what is left
var (chart, side)  = body.SplitRight(140d);

Tiles.Draw(surface, tiles, area: header);
Series.Chart(surface, series, area: chart);
VolumeProfile.Draw(surface, profile, area: side);
```

`Row(i, n)`, `Column(i, n)`, `SplitTop/Bottom/Left/Right`, `Inset(pad)`. Splits return the strip **and
the remainder**, so a layout reads top to bottom with no running offset to keep straight.

## Where the work goes

`Draw` runs on the **render thread**, when the host paints, and blocks the UI while it runs. Your data
callbacks run on a **pump thread** that may fire hundreds of times a second. So: compute in the
callback, keep only what the picture needs in a **bounded** buffer, and read that field in `Draw`.

```csharp
private const int Capacity = 240;                       // a visualizer lives as long as its window
private readonly List<Sample> _history = new(Capacity);

private void Record(Sample s)
{
    if (_history.Count == Capacity) _history.RemoveAt(0);
    _history.Add(s);
}
```

Reaching for context, market data or the clock inside `Draw` means the work is in the wrong place.

## Composing your own

When nothing in the tables fits:

```csharp
public void Draw(IRenderSurface surface)
{
    using var panel = surface.Panel("Delta", RenderPanelKind.Chart);
    if (_history.Count == 0) { Plot.Waiting(surface, "Waiting for bars…"); return; }

    var range = Plot.RangeOf(_history, s => s.Value).Padded();
    Plot.HorizontalGrid(surface, range);           // also declares the Y axis
    surface.AxisX(0d, Math.Max(1, _history.Count - 1));

    Series.Draw(surface, "Delta", _history, s => s.Value, range: range);
    Plot.Crosshair(surface, range);                // no-op when the pointer is elsewhere
}
```

**Panels stack** — open several in sequence for a chart with a histogram beneath it. Kinds `Chart`,
`Ladder`, `Matrix`, `Canvas` tell the host what chrome and default axes to supply.

**Series kinds**: `Line` for a continuous value, `Steps` for something that holds until it changes
(position, regime), `Bars` for per-interval quantities, `Area` for cumulative, `Scatter` for events.

**`PlotRange.Padded()` matters more than it looks** — it gives a *flat* range a usable width. A series
of identical prices is otherwise a zero-height range nothing can be plotted against, and the panel comes
out empty for a reason that is invisible in the code.

## Colour

Name roles, never RGB: `surface.Theme(RenderThemeColor.Bullish)`. The roles are `Text`,
`TextSecondary`, `Background`, `Surface`, `Grid`, `Border`, `Accent`, `Bullish`, `Bearish`, `Neutral`,
`Warning`.

A literal colour that looks right on your dark background is invisible on a light one, and a visualizer
cannot ask which theme is active — deliberately, because then it would have two appearances to get
right instead of one.

The **one** exception is `ColorScale`, for gradients a heatmap needs, and its ramps still start from
theme colours. Use `Diverging` for anything signed and `Sequential` only for a magnitude: a signed
quantity on a sequential ramp hides which side of zero it is on.

**Never let colour carry meaning alone.** Pair it with shape or position.

## What gets a picture rejected

- **Drawing nothing.** Use `Plot.Waiting`.
- **Unbounded history.** Draw is not where the leak shows up; the machine is, an hour later.
- **Work in `Draw`.** Averages, sorting, allocation. It runs per frame and blocks the UI.
- **Hard-coded colours**, per above.
- **A picture that disagrees with the book.** If a strategy took a position, the chart must show the
  signal it acted on. Confidently wrong is worse than blank.
- **Ignoring the cursor.** `Plot.Crosshair` is one line and makes a chart readable.

## The budget

The host bounds what one frame may emit and throttles a visualizer that draws unreasonably. Tens of
thousands of primitives per frame is the ceiling, not the target — a picture needing more than that
needs aggregating first, and a human cannot read it either.
