# What an authored unit cannot yet do

The goal loop's benchmark asks whether Hyperion can build, from a short brief, a unit that stands
beside the hand-written windows in `src/windows/Charts/`. Half of any shortfall would be
unattributable — is the SDK unable to express the window, or did the model simply not write it? So
the benchmark has a **control**: `samples/DaxAlgo.Sandbox.Samples/LiquidityBookVisualizer.cs`, a
hand-written authored answer to the same brief as `TradingTerminal.OrderBook`, written with full
knowledge of the SDK and no model involved.

Everything below is missing from the **contract**. A model cannot be marked down for any of it.
**The other half — what a model actually produced on the same brief — is
[`authored-unit-gaps-model-half.md`](authored-unit-gaps-model-half.md), and it is kept separate on
purpose: a contract shortfall and a model shortfall need different work.**

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
| Actions (verbs) | missing | **closed**, take-away included |
| Selection (pin a level) | impossible | **closed** |
| Zoom | missing | **closed** |
| Scrub / pan | missing | **closed** |
| Scrolling | missing | **closed for a ladder; no scrollbar** |
| Time axis on a captured series | missing | **missing** |
| Presets | missing | **missing** |
| Minimum size for a star panel beside a fixed one | — | **missing** (found 2026-08-31, second pass) |

Six of eight closed, one partly; a seventh turned out not to be a gap at all. The control now pins a price row on click and chooses its visible window from
the wheel and the drag, and the `heatWindow` parameter it needed for want of a gesture is gone.

**How, without handing the host a WPF type.** A click, a wheel notch and a drag are *transitions*, and
`Draw` is invoked twice per frame and must be pure — so a unit cannot consume one. The host therefore
**accumulates each gesture into state that stays put**, and the unit reads it: `Cursor.HasSelection`
with `SelectionX/Y`, and `Viewport.Zoom` / `PanX` / `PanY`. Data and reads, no callbacks and no
controls. That shape is the answer to the whole category, and the remaining gaps below are the ones it
does not reach.

## The gaps, most costly first

### 1. Verbs — closed for unit state 2026-08-31, open for anything that leaves the unit

The hand-written window has six: Export ladder CSV, Export series CSV, Save PNG, Save preset, Delete
preset, and a help popup. A unit could declare a *value* and nothing else; the nearest an author could
get was a bool parameter, which reads as a setting, behaves as a command, and has to be flipped twice
to mean "now".

`UnitAction(Id, Label, Detail?)` closes the half that stays inside the unit — reset the profile, clear
the tape, re-centre. Data, bounded at 8, malformed sets refused whole; the host renders buttons in the
setup expander and pressing one calls `OnActionAsync(id, …)`.

**An id and a callback, never a delegate the host holds.** A delegate would run wherever the host
called it — the render thread — so an action touching the same fields as a data callback would race
the pump. Going through the lifecycle lets the runtime invoke it under `_drawGate`, the same gate the
pump holds across every callback, so an author has one threading rule rather than two.

**Getting data OUT closed 2026-09-01.** `context.Export.Offer(label, text)` — the unit produces the
content, the host decides where it goes, and the host puts it on the **clipboard**. Deliberately not a
file: "export as CSV" means "get this data out of the window", and the clipboard satisfies that with
the sandbox gaining no filesystem reach at all — no paths, no overwrite, no disk quota, nothing to get
wrong about where untrusted code may write.

**Offers are honoured only while an action is running.** That is the whole safety argument, and it is
enforced in the runtime rather than advised: a unit cannot offer from a data callback, so nothing
reaches the viewer that they did not ask for by pressing a button. Bounded at 256 Ki characters and
rate-limited. `SandboxVisualizerActionTests` pins all of it, and removing the action gate fails the
test that matters.

Still host features, and never contract gaps: Save PNG, and the presets.

**And a verb sits behind one click**, because the setup expander is collapsed once a unit is running.
Right for parameters, which are reference material; arguable for a verb pressed often.

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

### ~~4. Scrolling~~ — closed for a ladder 2026-08-31, and the diagnosis was wrong the first time

Recorded as "an immediate-mode panel draws what fits". That was too broad. Once `Viewport.PanY`
existed a unit could already scroll by drag; what it could not do was scroll **a widget**, because
`LadderOptions` had `Levels` and no offset. Scrolling one meant handing `Ladder.Draw` a sliced
`DepthSnapshot` — two new lists built on the render thread every frame, which is the one thing the
drawing rules tell an author never to do.

`LadderOptions.FirstLevel` closes it: an index costs nothing and says the same. The control now
scrolls its book by drag, and running past the end of the book runs out of rows rather than throwing,
because a drag has no idea how deep the book is.

**Still missing: a scrollbar.** There is no affordance telling the viewer the book is scrollable or
how far down they are, and no widget but the ladder takes an offset.

### 5. The WIDGETS are index-based — and the first version of this entry was wrong

Recorded as "a unit has no way to ask the host how to place a column on a time axis". It has one.
`AxisX(minimum, maximum)` declares the range the coordinate transform maps through, so a unit that
declares ticks and draws at a timestamp is placed by **clock**, not by index. Pinned by
`PanelTitleTests.A_declared_axis_places_drawing_in_data_units_including_time`.

What is index-based is the widget **library**: `Series.Draw(surface, name, values)` and
`Heatmap.Draw(surface, columns, rows, …)` take arrays, so anything drawn through one is evenly spaced
whatever the clock did. A unit can have a true time axis by drawing raw — a `Series` scope with
`Push(x, y)`, or `Marker` — and cannot have one through a widget.

**Deliberately not "fixed" by making the control's trade dots time-positioned.** Its heatmap columns
come from a widget and are index-spaced; time-positioned dots over index-spaced columns would be
misaligned, which is worse than evenly-spaced-and-consistent. The whole picture has to agree, so this
is a widget-API question rather than a bug in the control.

| Cost of closing it | |
|---|---|
| Give the array widgets an optional x-position array | more SDK surface on every prompt |
| Leave it | a unit needing a true time axis hand-draws that panel |

### 6. A fixed-pixel panel starves its star sibling — examined, and deliberately left

Found by rendering the control at 320px wide: `Panel("Book", …).Pixels(240)` beside a `Star(3)` chart
leaves the chart **76 pixels**, and it keeps shrinking.

**Looked at properly, and there is no clean fix.** The obvious one — a `Minimum` on `PanelSize`,
mapped to the grid definition's `MinWidth` — does not do what it looks like it does. A WPF grid gives
an absolute column its size first and shares the remainder among the star columns; if that remainder
is smaller than a star column's `MinWidth` the grid **overflows and clips** rather than shrinking the
absolute one. So the star panel would go from "squeezed to 76px" to "200px and something cut off",
which is worse and harder to explain.

What would actually help is "fixed, but yield when there is no room", and a grid cannot express that
without changing what `Pixels` means: a star column capped by `MaxWidth` takes the right size when
space is tight and under-fills when it is not.

Left as it is, with the trade-off written down. The hand-written window has the same shape and the
same behaviour, so it is not a regression against the benchmark, and it only bites at window sizes
nobody uses.

### 7. No presets

Named snapshots of the parameter set, saved and reapplied. The host owns the parameter values, so
this is a host feature that simply has not been built, rather than a contract gap.

## ~~8. A picture could not move~~ — closed 2026-09-01

Not found against the order-book control, which is a still picture and correct as one. It came from the
restated goal: an order book drawn as a battlefield needs soldiers that move, and **a unit had no way
to know how much time had passed.** `Draw` is pure, is invoked more than once per frame, and had no
clock; the guidance tells an author never to reach for one there, and it was right to.

Closed the same way selection and zoom were: **the host owns it, the unit reads it.** `surface.Now` is
the instant the frame is being drawn at.

**It is a timestamp, not a "time since this unit appeared", and that was the whole design question.**
An elapsed-since-start counter is the more obvious API and it cannot express the thing units actually
need. A unit does not usually want abstract motion; it wants *"that sweep printed 0.8 seconds ago, draw
it fading"*. The stamp for that has to be taken in a **data callback**, which runs on the pump thread
and cannot read a render clock — so the only workable primitive is one both sides share. `surface.Now`
is the same clock as `context.Clock.UtcNow`, so the whole animation is one subtraction:

```csharp
// in OnTradeAsync — this is where the event happened
if (trade.Size >= _sweepSize) _lastSweepAt = context.Clock.UtcNow;

// in Draw — derived, never accumulated
var age = surface.Now - _lastSweepAt;
var alpha = 1d - age.TotalSeconds / FadeSeconds;
```

Two host properties make it true rather than advised:

- **One frame is one instant.** The view samples the clock once and hands the same value to the
  discovery pass and the drawing pass. A surface that read the clock itself would give one frame two
  times, and a unit whose panel count varied with time would be laid out against a frame it never drew
  — the same failure the pointer-blind discovery pass exists to prevent, with a different input.
- **One unit is one clock.** `AuthoredUnitLayoutHost` applies it to every panel, including panels
  already built when the clock arrives. Per-panel clocks are the obvious implementation and drift: the
  views are constructed milliseconds apart, so two panels animating the same thing would sit
  permanently out of phase and look like an authoring mistake.

Both are pinned by `AuthoredWindowClockTests`, and the seam is pinned through the shell composition by
`AuthoredVisualizerCompositionTests.The_units_own_clock_reaches_the_window` — which fails when the
argument is dropped **and** when a window starts a clock of its own, because the fixture's clock is a
fixed instant nowhere near a wall clock.

**Nothing had to be built to make frames arrive.** `AuthoredUnitHost` already runs a render timer that
requests a frame on an interval, so a unit that draws from `Now` animates without asking for anything.
The cost is that `Draw` now runs whether or not data arrived, which was already true.

`DrawProbe` draws its frame thirty seconds after the drive's clock rather than at
`DateTime.MinValue` — at the origin, `Now - stampedAt` is negative by two thousand years, so a fade
would come out at an enormous alpha and a derived position would be non-finite. The probe would have
reported a unit broken by the probe.

Demonstrated in `BookPressureVisualizer`, in the same change, per the rule that
**an exemplar is what a model copies**: a sweep print marks the right edge and the mark fades over 1.5
seconds. Still open: nothing yet measures whether a model picks it up.

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

## What closing gaps costs the prompt

The brief predicted this and it is worth keeping measured: *"the generated SDK surface is generated
from the SDK, so it grows as you do the rest of this work."* It does. Every capability added to close
a gap here appears in the surface, and the contract sections are never rationed.

| | Deep prompt, order-book brief |
|---|---:|
| before the surface cut | 112,219 |
| after the surface cut | 94,435 |
| after verbs + the exemplar demonstrating them | **99,070** |

So two iterations of capability have spent 4,635 of the 17,784 the cut bought.

**The exemplar is not the next lever, which was measured rather than assumed.** It is 13,306
characters and unrationed, and that looked like the obvious place to squeeze — but it is **72% code**:
XML docs are 15.6% and inline comments 12.0%. Stripping every comment would save 3.7 KB on a 99 KB
prompt and cost the model the commenting style the exemplar exists to teach. There is no cheap win
there; the remaining lever is the surface budget, whose curve is in `SdkSurfaceSelector`.

## Three things the contract promised and did not do

Found 2026-08-31 by checking what `surface.Panel(title, kind)` and `AxisX(min, max, format)` actually
carry. All three were stored on the panel slot and read by nothing:

| Promised | Actually |
|---|---|
| `RenderPanelKind` — "the host picks chrome, gutters and default axes from this" | read by nothing |
| `AxisX`/`AxisY` `format` — "optional numeric/date format" | stored, never rendered; labels belong to the widget |
| `Panel(title, …)` — the title | stored, never drawn |

The drawing pack repeated the first one to every model that asked for a picture: *"kinds tell the host
what chrome and default axes to supply."*

**The title is now drawn** — it is the one worth making true, because a `UnitLayout` body gets a real
header per panel while a unit dividing ONE surface with several `Panel` scopes (which is what both
exemplars do, and therefore what a generated unit copies) got unlabelled regions. It is not charged to
the frame budget: that budget bounds what untrusted code emits, and charging a unit for host chrome
would let a decoration change push a well-behaved visualizer over the limit.

**The other two are now documented as what they are.** Axis labels genuinely belong to the widget —
having the host draw them too would double every label on every chart — so the honest fix there was the
sentence, not the feature.

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

So the harness is sound from source text to painted window.

**The model has now been run too, twice — 2026-08-31 and 2026-09-01.** The drive is committed as
`HyperionBenchmark`, and what came out is in
[`authored-unit-gaps-model-half.md`](authored-unit-gaps-model-half.md). Short version: on a free-tier
model, one line of brief produced a 441-line three-panel order-book visualizer that compiles in two
generations and clears the whole ladder, with the microstructure maths taken from the library and both
verbs wired — and **no gestures whatsoever**, which is a teaching gap rather than a contract one.

That run also found that the ladder had been judging the wrong method for every unit that declares a
layout. See the same file.

One caution the run produced: the panel has to be a realistic size before any of this means anything.
At 320px the control's chart panel is 76px wide and a click lands wherever geometry puts it — the
first version of that assertion failed for exactly that reason and was measuring the test, not the
window.
