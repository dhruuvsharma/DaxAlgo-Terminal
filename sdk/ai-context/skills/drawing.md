---
id: drawing
name: Drawing — the widget library, and composing your own
triggers: window, view, ui, panel, chart, display, show, render, dashboard, visual, plot, gui, screen, draw, drawing, candle, candlestick, ladder, footprint, heatmap, graph, picture, paint, depth, profile, tape, equity, gauge, table, tile, band, histogram, signal, level, legend, volume
---

# Drawing

**There is no control, no XAML, no window.** `Draw` gets an `IRenderSurface` and nothing else. That is
what lets a stranger's visualizer run in this process at all. (Immediate mode and the frame contract
are in the SDK surface above; this pack is the library and the judgement.)

A body split into several panels — a chart beside a book, two venues side by side — is still just an
`IRenderSurface` per panel, and everything below applies unchanged. See the **layout** pack.

## Reach for a widget before you draw anything by hand

**Check this table first** — hand-rolling one of these is slower to write, longer to read, and fails
verification in ways the widget already handles (blank first frame, a flat series that divides by
zero, colour used where shape was needed, a scale that flatters the data).

Each takes an options record. **Pass `Default`, or omit the argument.** Never `new()` — on a record
struct that binds the implicit parameterless constructor, every field lands on zero, and a zero-width
transparent widget looks exactly like a broken one. To change one field:
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

- **`Plot.Waiting(surface, "…")`** is the empty state in one line:
  `if (_history.Count == 0) { Plot.Waiting(surface); return; }`. Drawing nothing is the commonest way a
  picture fails review, and a blank panel reads as a broken application.
- **One scale across a comparison.** `Series.Chart` scales every series together; separately-scaled
  series look like they agree when they do not, and that chart looks exactly like a correct one.
- **A histogram's baseline stays in range**; **both sides of the book share one size scale** in
  `DepthCurve`, since a lopsided book looking balanced is what that picture exists to reveal.
- **Shape as well as colour on `Signals`**: buy triangle, sell diamond, exit cross.
- **`VolumeProfile.ValueArea(rows)`** returns the same low/high/POC the picture drew, so a strategy can
  trade the levels its own chart shows.
- **Flat series, single points and tiny panels** produce no NaN.

## Placing widgets: `PlotArea`

Every widget takes an `area`. Omitted it fills the panel; given one it is confined to that rectangle.

**A split returns `(strip, remainder)` — the strip FIRST.** Named the other way round the picture comes
out inside out, and it compiles, draws and passes every test.

```csharp
var (header, body)  = PlotArea.Of(surface).SplitTop(56d);
var (book, chart)   = body.SplitRight(140d);   // book is the 140px strip
var (delta, prices) = chart.SplitBottom(80d);  // delta is the 80px strip
```

`Row(i, n)`, `Column(i, n)`, `SplitTop/Bottom/Left/Right`, `Inset(pad)` — strip first every time, so a
layout reads top to bottom with no running offset to keep straight.

## Where the work goes

`Draw` runs on the **render thread** and blocks the UI while it runs. Your data callbacks run on a
**pump thread** that may fire hundreds of times a second. So: compute in the callback, keep only what
the picture needs in a **bounded** buffer (fixed capacity, drop the oldest), and read that field in
`Draw`. Reaching for context or market data inside `Draw` means the work is in the wrong place.

**`Draw` is invoked more than once per frame**, so it must be pure: never advance a counter, append to
a list, or latch a state there. Everything the host offers it is a *read* for exactly that reason —
`surface.Viewport`, `surface.Cursor`, and `surface.Now`.

## Putting a frame together

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

**Panels stack** — open several in sequence, each titled in its corner. The host supplies no axes:
`AxisX`/`AxisY` declare the range your coordinates are in, and the LABELS come from whichever widget
you hand a format to. **Series kinds**: `Line` for a continuous value, `Steps` for what holds until it
changes, `Bars` for per-interval quantities, `Area` for cumulative, `Scatter` for events.

**`PlotRange.Padded()` gives a *flat* range a usable width.** Identical prices are otherwise a
zero-height range nothing can plot against, and the panel comes out empty for a reason invisible in
the code.

## No widget fits, including in three dimensions

A brief can ask for a picture nothing above resembles. **Do not substitute the nearest widget and do
not refuse** — `Line`, `Rect`, `Marker` and `Text` draw anything. Work out the geometry, map your data
into panel coordinates, and emit primitives, one small method per shape.

There is no 3D surface and there will not be one, because a unit never touches a control. **Project
it yourself:**

```csharp
var view = Projection3.Of(Camera3.Default, area.Width, area.Height);  // .Orbit(seconds) to turn
foreach (var row in rowsOldestFirst)                                  // FAR TO NEAR
{
    Projected p = view.Project(new Vec3(x, height, z));
    if (!p.InFront) continue;                    // behind the camera projects to a plausible LIE
    surface.Line(area.X + prev.X, area.Y + prev.Y, area.X + p.X, area.Y + p.Y);
}
```

Sort by `Projected.Depth` **descending** so nearer things are drawn last and cover what is behind
them; always test `InFront`; size the projection from `surface.Viewport` and return early when it has
no area; and fade older rows with `Alpha` rather than inventing a lighting model. Exact for markers
and for a height field walked back to front — it sorts *wrongly* for shapes that interpenetrate, and
no ordering fixes that.

## Colour

Name roles, never RGB: `surface.Theme(RenderThemeColor.Bullish)`. The roles are `Text`,
`TextSecondary`, `Background`, `Surface`, `Grid`, `Border`, `Accent`, `Bullish`, `Bearish`, `Neutral`,
`Warning`. A literal that looks right on your dark background is invisible on a light one, and you
cannot ask which theme is active — deliberately, or you would have two appearances to get right.

The **one** exception is `ColorScale`, for gradients a heatmap needs, and its ramps still start from
theme colours. Use `Diverging` for anything signed and `Sequential` only for a magnitude: a signed
quantity on a sequential ramp hides which side of zero it is on.

**Never let colour carry meaning alone.** Pair it with shape or position.

## Gestures

Every pointer gesture arrives as **accumulated state you read**, never an event you handle — `Draw`
runs more than once per frame and must be pure, so it cannot consume a click or a notch just once.
All four are panel-local.

| Viewer | You read | Use it for |
|---|---|---|
| hovers | `Cursor.IsInside`, `.X`, `.Y` | crosshair + readout (`Plot.Crosshair`) |
| clicks | `Cursor.HasSelection`, `.SelectionX/Y` | invert your axis mapping, highlight that row |
| wheel | `Viewport.Zoom` (1 = unzoomed) | `visible = window / Zoom` |
| drags | `Viewport.PanX`, `.PanY` (pixels) | offset which slice you show — a ladder takes `FirstLevel` |

**Apply `Zoom` to your data range, never your coordinates** — scaling the drawing magnifies the text
with it. A pin survives the pointer leaving, which is when someone is reading it. And do not add a
parameter for something that is now a gesture.

## What gets a picture rejected

- **Drawing nothing.** Use `Plot.Waiting`.
- **Unbounded history.** Draw is not where the leak shows up; the machine is, an hour later.
- **Work in `Draw`.** Averages, sorting, allocation. It runs per frame and blocks the UI.
- **Hard-coded colours**, per above.
- **A picture that disagrees with the book.** If a strategy took a position, the chart must show the
  signal it acted on. Confidently wrong is worse than blank.
- **Ignoring the cursor.** See gestures; `Plot.Crosshair` is one line.

## The budget

The host bounds what one frame may emit and throttles a visualizer that draws unreasonably. Tens of
thousands of primitives is the ceiling, not the target: a picture needing more needs aggregating
first, and a human cannot read it either.
