---
id: drawing
name: Drawing — panels, series, and the four ready-made pictures
triggers: window, view, ui, panel, chart, display, show, render, dashboard, visual, plot, gui, screen, draw, drawing, candle, candlestick, ladder, footprint, heatmap, graph, picture, paint
---

# Drawing

You describe the whole frame every time you are asked, and the host retains nothing between frames.
That suits streaming market data, where most of the picture changes on every tick, and it means you
hold no visual state the host has to reconcile.

**There is no control, no XAML, no window.** `Draw` gets an `IRenderSurface` and nothing else. That is
what lets a stranger's visualizer run in this process at all.

## Before anything else: where the work goes

`Draw` runs on the **render thread**, when the host paints, and blocks the UI while it runs. Your data
callbacks run on a **pump thread** that may fire hundreds of times a second.

```csharp
// In the callback: compute, and keep only what the picture needs.
public Task OnBarAsync(OhlcvBar bar, IVisualizerContext ctx, CancellationToken ct)
{
    if (bar.IsFinal) Record(new Sample(bar.Close, Average(ctx)));
    return Task.CompletedTask;
}

// Bounded, always. A visualizer lives as long as its window.
private const int Capacity = 240;
private readonly List<Sample> _history = new(Capacity);

private void Record(Sample sample)
{
    if (_history.Count == Capacity) _history.RemoveAt(0);
    _history.Add(sample);
}

// In Draw: read the field. Nothing else.
public void Draw(IRenderSurface surface) { ... }
```

Reaching for context, market data or the clock inside `Draw` means the work is in the wrong place.

## The four ready-made pictures

Before drawing primitives by hand, check whether one of these is what you want. Each takes an options
record; pass `Default` or omit it.

| Want | Use | Notes |
|---|---|---|
| OHLC candles | `Candles.Draw(surface, bars)` | Auto-scales the price axis from the bars and returns the range it used |
| Depth ladder | `Ladder.Draw(surface, depth)` | Asks above bids, best prices meeting in the middle |
| Volume footprint | `Footprint.Draw(surface, bars)` | Bars as columns, price as rows, buy/sell split per cell |
| Grids, axes, crosshair | `Plot.*` | The furniture under everything else |

**`Ladder` scales bar length to the largest size *in view*, not the whole book** — a ladder scaled to a
far-touch iceberg shows nothing at the touch, which is where the attention is. **`Footprint` shades
cells per bar, not across the window**, because one high-volume bar would otherwise wash out every
other column and the distribution *within* each bar is the reason to look at a footprint at all.

Options records have an explicit `Default`. Use it rather than `new()`: on a record struct `new()`
binds to the implicit parameterless constructor, every field lands on zero, and the routine draws
nothing.

## Composing your own

```csharp
public void Draw(IRenderSurface surface)
{
    using var panel = surface.Panel("Delta", RenderPanelKind.Chart);

    if (_history.Count == 0)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
        surface.Text(8d, 20d, "Waiting for bars…");
        return;
    }

    var range = PlotRange.Empty;
    for (var i = 0; i < _history.Count; i++) range = range.Include(_history[i].Value);
    range = range.Padded();          // never let data sit flush against the edge

    Plot.HorizontalGrid(surface, range);              // also declares the Y axis
    surface.AxisX(0d, Math.Max(1, _history.Count - 1));

    surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
    using (var series = surface.Series("Delta", RenderSeriesKind.Line))
    {
        for (var i = 0; i < _history.Count; i++) surface.Push(i, _history[i].Value);
    }

    Plot.Crosshair(surface, range);   // no-op when the pointer is elsewhere, so call it unconditionally
}
```

**Panels stack.** Open several in sequence for a chart with a histogram beneath it. Panel kinds —
`Chart`, `Ladder`, `Matrix`, `Canvas` — tell the host what chrome, gutters and default axes to supply.

**Series kinds**: `Line` for a continuous value, `Steps` for something that holds until it changes
(position, regime), `Bars` for per-interval quantities (volume, delta), `Area` for cumulative,
`Scatter` for discrete events.

**`PlotRange.Padded()` matters more than it looks.** It also gives a *flat* range a usable width — a
series of identical prices is otherwise a zero-height range that nothing can be plotted against, and
the panel comes out empty for a reason that is invisible in the code.

## Colour

Name roles, never RGB: `surface.Theme(RenderThemeColor.Bullish)`. The roles are `Text`,
`TextSecondary`, `Background`, `Surface`, `Grid`, `Border`, `Accent`, `Bullish`, `Bearish`, `Neutral`,
`Warning`.

A literal colour that looks right on your dark background is invisible on a light one, and a visualizer
cannot ask which theme is active — deliberately, because then it would have two appearances to get
right instead of one.

**Never let colour carry meaning alone.** Pair it with shape or position: roughly one man in twelve
cannot separate the bullish and bearish roles reliably.

## What gets a picture rejected

- **Drawing nothing.** The commonest failure, and invisible — a blank panel looks like a broken host
  rather than an empty visualizer. If you have no data yet, say so with `Text`.
- **Unbounded history.** Draw is not where the leak shows up; the machine is, an hour later.
- **Work in `Draw`.** Averages, sorting, allocation. It runs per frame and blocks the UI.
- **Hard-coded colours**, per above.
- **A picture that disagrees with the book.** If a strategy took a position, the chart must show the
  signal it acted on. Confidently wrong is worse than blank.
- **Ignoring the cursor.** `Plot.Crosshair` is one line and makes a chart readable; there is no reason
  to omit it.

## The budget

The host bounds what one frame may emit and will throttle a visualizer that draws unreasonably rather
than trust it. Tens of thousands of primitives per frame is the ceiling, not the target. If a picture
needs more than that, it needs aggregating first — and a human cannot read it either.
