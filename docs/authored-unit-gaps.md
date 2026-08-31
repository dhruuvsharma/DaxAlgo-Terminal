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

So the picture was never the gap. The gap is everything the user *does* to the picture.

## Delta

| | 2026-08-31, first run | after the gesture work |
|---|---|---|
| Actions (verbs) | missing | **missing** |
| Selection (pin a level) | impossible | **closed** |
| Zoom | missing | **closed** |
| Scrub / pan | missing | **closed** |
| Scrolling | missing | **missing** |
| Time axis on a captured series | missing | **missing** |
| Presets | missing | **missing** |
| Minimum size for a star panel beside a fixed one | — | **missing** (found 2026-08-31, second pass) |

Three of eight closed. The control now pins a price row on click and chooses its visible window from
the wheel and the drag, and the `heatWindow` parameter it needed for want of a gesture is gone.

**How, without handing the host a WPF type.** A click, a wheel notch and a drag are *transitions*, and
`Draw` is invoked twice per frame and must be pure — so a unit cannot consume one. The host therefore
**accumulates each gesture into state that stays put**, and the unit reads it: `Cursor.HasSelection`
with `SelectionX/Y`, and `Viewport.Zoom` / `PanX` / `PanY`. Data and reads, no callbacks and no
controls. That shape is the answer to the whole category, and the remaining gaps below are the ones it
does not reach.

## The gaps, most costly first

### 1. A unit can declare a parameter; it cannot declare a verb

The hand-written window has six actions: Export ladder CSV, Export series CSV, Save PNG, Save
preset, Delete preset, and a help popup. There is no affordance anywhere in the SDK for *a button
that does something*. `IParameters` is read-only values; the host chrome builds an expander of
controls from `StrategyParameterSchema` and nothing else.

This is the single largest difference by volume, and it is a contract gap rather than a rendering
one: an action is data (a name, a group, an enablement) plus a callback, which is exactly the shape
the layout tree already uses, so it does not require handing the host a WPF type.

### ~~2. Selection~~ — closed 2026-08-31

Kept because the diagnosis is the reusable part. It was impossible for two compounding reasons:

- **`RenderCursor.IsPressed` was sampled on `MouseMove` only.** `RenderSurfaceView` handled
  `MouseMove` and `MouseLeave` and nothing else, so a press that did not move was invisible and a
  release that did not move never cleared. Not coarse — *wrong*, until the pointer next moved.
- **A click is a transition, and `Draw` may not observe one.** `OnRender` invokes the draw callback
  **twice** per frame — a discovery pass to count panels, then the real pass — and the code says so:
  *"This is why Draw MUST BE PURE."* So a unit may not latch a press-to-release transition.

The fix was the second point taken seriously: the host accumulates, the unit reads. `MouseDown` /
`MouseUp` now exist (fixing `IsPressed` outright), a press-and-release that travels less than 4px is a
click, and the click becomes a sticky `Cursor.HasSelection` + `SelectionX/Y`, mapped per panel and
surviving the pointer leaving.

A third thing fell out: the **discovery pass is now pointer-blind**. It had been given the live
cursor, so a unit branching on `IsInside` would open a different number of panels on the two passes of
one frame and every panel would get the wrong share of the height — a layout that rearranged as the
mouse moved. Panel structure must not depend on pointer state, and a blank cursor in discovery is what
makes that true rather than merely advised.

### ~~3. Zoom and scrub~~ — closed 2026-08-31

`Viewport.Zoom` (wheel, compounding at 1.2 per notch, clamped to [0.25, 32]) and `Viewport.PanX/PanY`
(drag, in panel pixels). Both accumulate on the host for the same reason as the selection.

**Apply zoom to the data range, not to the coordinates** — the control divides its column window by
it. Scaling the drawing would magnify the text and line widths with it.

### 4. No scrolling

The hand-written ladder is a `ScrollViewer` over every level in the book. An immediate-mode panel
draws what fits. `LevelsParameter` is the workaround, and it is not the same thing: a 30-level cap is
a different product from a scrollable book.

### 5. No time axis on a captured series

The control captures one heatmap column per depth snapshot, so a column is a capture tick rather
than a clock interval. A unit has no way to ask the host how to place a column on a time axis, so
trade dots are positioned by index and not by their own timestamp. The picture is right in shape and
wrong in spacing whenever the book updates unevenly — which is always.

### 6. A fixed-pixel panel has no minimum for its star sibling

Found by rendering the control at 320px wide: `Panel("Book", …).Pixels(240)` beside a `Star(3)` chart
leaves the chart **76 pixels**, and it keeps shrinking. `UnitLayout` has `Star` and `Pixels` and no way
to say "this panel needs at least N, drop me below that and stack instead". The hand-written window has
the same shape and the same behaviour, so this is not a regression against the benchmark — but it is a
thing an author cannot express, and the smallest of the gaps here.

### 7. No presets

Named snapshots of the parameter set, saved and reapplied. The host owns the parameter values, so
this is a host feature that simply has not been built, rather than a contract gap.

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

## What has actually been run

Everything except the model. On 2026-08-31 the control was driven the whole way down the pipeline a
generated unit takes, and it came through clean at every step:

| Step | Result |
|---|---|
| Compile through `RoslynStrategyCompiler` (the sandbox path, policy scan included) | passes, as written — namespace and usings and all |
| Verification ladder, all rungs | Lifecycle · SchemaCoherence · DrawProbe pass; Replay N/A for a visualizer |
| Preview | builds |
| The real three-panel window via `AuthoredUnitLayoutHost` | every panel paints after a full drive |
| A click on the liquidity panel | changes what that panel paints — the pin reaches the picture, not just the flag |

So the harness is sound from source text to painted window. The remaining risk is concentrated in the
one part that cannot be run here: **what a model actually produces**. That needs a live provider and a
key, and is still not done.

One caution the run produced: the panel has to be a realistic size before any of this means anything.
At 320px the control's chart panel is 76px wide and a click lands wherever geometry puts it — the
first version of that assertion failed for exactly that reason and was measuring the test, not the
window.
