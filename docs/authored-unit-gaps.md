# What an authored unit cannot yet do

The goal loop's benchmark asks whether Hyperion can build, from a short brief, a unit that stands
beside the hand-written windows in `src/windows/Charts/`. Half of any shortfall would be
unattributable — is the SDK unable to express the window, or did the model simply not write it? So
the benchmark has a **control**: `samples/DaxAlgo.Sandbox.Samples/LiquidityBookVisualizer.cs`, a
hand-written authored answer to the same brief as `TradingTerminal.OrderBook`, written with full
knowledge of the SDK and no model involved.

Everything below is missing from the **contract**. A model cannot be marked down for any of it.

Measured 2026-08-31 against `TradingTerminal.OrderBook` (1,154-line view-model, 492-line XAML,
448-line code-behind).

## What the control reached

Compiled first attempt and clears the verification ladder — rungs 6, 5 and 7 — with the same
arrangement as the hand-written window: heatmap dominant, ladder as a fixed column beside it, tile
strip beneath. Reachable in one call each: the depth ladder, the liquidity heatmap, the microprice
path, trade dots at their own price, the signed imbalance lane, and the microstructure tiles
(mid, edge, queue, flow, toxicity, sweep cost, spread z-score).

**A crosshair and a hover readout also work**, which was not obvious going in. `RenderCursor` is a
read rather than an event, and `RenderSurfaceView` invalidates on `MouseMove`, so a unit can draw a
crosshair and price/size readout at the pointer with no host round-trip.

So the picture is not the gap. The gap is everything the user *does* to the picture.

## The gaps, most costly first

### 1. A unit can declare a parameter; it cannot declare a verb

The hand-written window has six actions: Export ladder CSV, Export series CSV, Save PNG, Save
preset, Delete preset, and a help popup. There is no affordance anywhere in the SDK for *a button
that does something*. `IParameters` is read-only values; the host chrome builds an expander of
controls from `StrategyParameterSchema` and nothing else.

This is the single largest difference by volume, and it is a contract gap rather than a rendering
one: an action is data (a name, a group, an enablement) plus a callback, which is exactly the shape
the layout tree already uses, so it does not require handing the host a WPF type.

### 2. Selection is impossible, and the reason is structural

Clicking a price level to pin it is the order book's central gesture. It cannot be written, for two
reasons that compound:

- **`RenderCursor.IsPressed` is sampled on `MouseMove` only.** `RenderSurfaceView` handles
  `MouseMove` and `MouseLeave` and nothing else, so a press that does not move is invisible and a
  release that does not move never clears. The value is not merely coarse, it is wrong until the
  pointer next moves.
- **A click is a transition, and `Draw` may not observe one.** `OnRender` invokes the draw callback
  **twice** per frame — a discovery pass to count panels, then the real pass — and the code says so:
  *"This is why Draw MUST BE PURE."* So a unit may not latch a press-to-release transition in the
  only place it can see the cursor. Any author who tries gets double-fires.

A selection therefore needs host-side state, not an author-side latch: something like a
`RenderCursor.Click` that the host raises once per real click and clears after the frame, or a
host-owned selected-point the unit reads.

### 3. No zoom, no scrub

No wheel, no drag anchor, no delta. In the control, the heat window is a **parameter** because it
cannot be a gesture — the user retypes a number where the hand-written window would scroll. Same for
the price range: fixed to the window extremes.

### 4. No scrolling

The hand-written ladder is a `ScrollViewer` over every level in the book. An immediate-mode panel
draws what fits. `LevelsParameter` is the workaround, and it is not the same thing: a 30-level cap is
a different product from a scrollable book.

### 5. No time axis on a captured series

The control captures one heatmap column per depth snapshot, so a column is a capture tick rather
than a clock interval. A unit has no way to ask the host how to place a column on a time axis, so
trade dots are positioned by index and not by their own timestamp. The picture is right in shape and
wrong in spacing whenever the book updates unevenly — which is always.

### 6. No presets

Named snapshots of the parameter set, saved and reapplied. The host owns the parameter values, so
this is a host feature that simply has not been built, rather than a contract gap. Cheapest of the
six.

## What is NOT a gap

- **Instrument selection.** `StrategyParameter.Instrument` and the host picker cover it.
- **View toggles.** Bool parameters give checkboxes; the control uses four.
- **Pause.** Expressible as a bool parameter, though the hand-written version is better: it freezes
  the display while the stream keeps running, so resume is instant.
- **Theming.** `surface.Theme(...)` covers it, and better than the hand-written window, which uses a
  fixed palette and ignores the theme entirely.
- **The maths.** `Book.Microprice`, `Book.Imbalance`, `Book.SweepSlippage`, `TradeClassifier`,
  `OrderFlowImbalance`, `Vpin`, `SpreadStats` — every statistic in the microstructure strip is one
  call.

## What the hand-written window has that is out of scope

Its ML micro-forecaster (`OrderBookMicroPredictor`, 527 lines: online logistic and linear learners,
a warm start from stored depth, rolling Brier and MAE against a baseline) is a research feature, not
a window feature. An authored unit could compute the same thing in its callbacks; nothing in the
contract stops it. It is listed here only so the line-count comparison is not read as a gap.

## Next

The gaps above are the SDK half of the benchmark. The other half — what a model fails to produce
even where the contract allows it — needs a live provider run and has not been done.
