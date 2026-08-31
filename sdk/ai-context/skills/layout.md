---
id: layout
name: Layout — dividing the window into panels
triggers: layout, panel, panels, dashboard, split, side by side, beside, alongside, two charts, multiple charts, arbitrage, ladder and chart, book and chart, arrange, arrangement, columns, rows, grid, pane, panes, separate, stacked, top and bottom, left and right, multi
---

# Layout

By default a unit gets **one panel** filling the body, painted by your `Draw`. That is the right answer
for most units and you should not override it.

When the request genuinely needs several panels — a chart beside an order book, two venues with a
spread strip between them — describe them with `UnitLayout`:

```csharp
public UnitLayout Layout => UnitLayout.Columns(
    UnitLayout.Panel("Price", DrawChart).Star(3),
    UnitLayout.Panel("Book",  DrawBook).Pixels(260));

private void DrawChart(IRenderSurface s) { /* this panel only */ }
private void DrawBook(IRenderSurface s)  { /* this panel only */ }
```

Each panel gets its **own surface**: its own viewport, its own cursor, a header, and a separator the
user can drag. Your `Draw` is then unused — the panels do the drawing.

**Write `UnitLayout.` and not `Layout.`** The property is called `Layout`, so inside the class that
identifier binds to the property rather than to the static class, and `Layout.Rows(...)` does not
compile. `UnitLayout` is never a member name, so it never shadows.

## The vocabulary

- `UnitLayout.Panel(title, draw)` — one panel. Pass no title for a full-bleed panel with no header.
- `UnitLayout.Rows(...)` — stacked top to bottom.
- `UnitLayout.Columns(...)` — placed left to right.
- `.Star(n)` — takes `n` shares of the space left over. The default is one share.
- `.Pixels(n)` — an exact height (in rows) or width (in columns). What a ladder or a status strip wants.

Rows and columns nest, so any arrangement is reachable:

```csharp
// Chart on top; book and tape sharing the space beneath it.
public UnitLayout Layout => UnitLayout.Rows(
    UnitLayout.Panel("Price", DrawChart).Star(2),
    UnitLayout.Columns(
        UnitLayout.Panel("Book", DrawBook),
        UnitLayout.Panel("Tape", DrawTape)));
```

## What you do not build

**No WPF, no XAML, no controls of any kind.** A layout is data and draw callbacks; the host builds
every pixel of the chrome. The seam that once let a unit hand over a WPF element was removed — an
element built by an author runs inside the application, which ends the isolation that lets a
stranger's unit run at all.

**Not the parameter expander or the activity log.** Those sit above and below your body and are
host-owned. Every unit gets them, identically, and a layout cannot move or omit them. A visualizer
that declares no parameters simply shows no expander.

## Limits, and what happens at them

At most **16 panels** and **6 levels** of nesting. Past either, or if any split is empty or a panel has
no draw callback, the whole layout is refused and the unit falls back to its single panel — so a
mistake costs the arrangement, never the window. Ten panels is already a hard window to read; if you
are near the ceiling, the design is usually wrong rather than the ceiling.

## When not to use it

To divide **one** picture — a price panel above a volume strip in the same chart — use
`PlotArea.SplitTop` and friends inside a single `Draw`. That is a drawn subdivision, and it is right
when the parts share an x-axis and must scroll together. Reach for `UnitLayout` when the parts are
genuinely separate views the user might want to resize independently.
