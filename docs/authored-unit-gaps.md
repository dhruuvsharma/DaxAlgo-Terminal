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
| Time axis on a captured series | missing | **closed 2026-09-02** |
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

### ~~5. The WIDGETS are index-based~~ — closed 2026-09-02

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

**Closed by giving the series family an optional position array**, and the cost line above was wrong.
It said "more SDK surface on every prompt"; `Series` and `PlotArea` are in `DaxAlgo.Sdk.Drawing`, which
this document's own cost table records as RATIONED — so a brief that never asks for a picture pays
nothing. Half the entries here have been wrong when measured, and this was one of them.

`PlotArea.ToY` had mapped a value through a range all along; `ToX` only ever mapped an index, and that
asymmetry WAS the gap. `ToX(value, range)` closes it, and `Series.Draw(..., at:)` /
`SeriesData.Line(..., at:)` carry the positions through.

Four properties, each asserted on the pushed COORDINATES rather than on the call returning:

- a long gap between samples dominates the panel instead of being one step like any other;
- with no positions, spacing is unchanged — the default stays right for a bar series, where a column
  IS an interval, and every existing unit is on that path;
- positions that do not cover the series are ignored outright, because half a series against the clock
  and half against nothing is worse than none of it and hides a caller bug;
- `Chart` computes ONE x range across every series and declares it through `AxisX`. Two series over
  different spans would otherwise each fill the panel and cross at a point that means nothing, and the
  host maps a pointer back through the declared axis — an index range under time-placed points is how
  a crosshair reads the wrong value.

Still index-spaced, correctly: `Heatmap` and `Footprint`, where a column is an interval rather than a
sample. The doc's warning that "the whole picture has to agree" is why this stopped at the series
family rather than being applied everywhere.

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

### ~~8. A picture could not move~~ — closed 2026-09-01

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

### ~~9. 3D was not expressible at all~~ — closed 2026-09-01

The hand-written 3D windows used HelixToolkit. An authored unit gets 2D primitives, and the hard
constraint stands: no `FrameworkElement`, no WPF type, data and callbacks only.

**The answer is arithmetic, not a renderer.** `Projection3.Of(camera, width, height).Project(point)`
turns a world point into a panel point; the unit sorts by `Projected.Depth` descending and draws far
to near. Painter's algorithm, four types, no scene, no mesh, no light, no z-buffer. **Nothing new
reaches the host** — `RenderSurfaceView`, the frame budget and the two-pass discovery are untouched,
which is the whole reason this is affordable at all.

#### The cost, and placement is the entire answer

`SdkSurfaceGenerator.Section()` sorts a type by namespace and `SdkSurfaceSelector` may only ration two
of the five sections. So:

| Where a type lives | Charged |
|---|---|
| `IRenderSurface` (a contract section) | every prompt, always — `surface.Now` costs 1,914 that way |
| **anything outside `Quant`/`Drawing`** — including `DaxAlgo.Sdk` itself | every prompt, always. This is the trap |
| `DaxAlgo.Sdk.Quant` or `DaxAlgo.Sdk.Drawing` | rationed: a capped index line unless the brief asks |

All four types are in `DaxAlgo.Sdk.Quant`. Measured on the order-book brief, which does not mention
3D: **516 characters, 0.45% of the prompt.** The blocks themselves total 2,300.

Two estimates in the design were wrong and are recorded in
`.claude/context/tasks/2026-09-01-1900-3d-projection-design.md`. The instructive one: an options record
is *compacted* to a line and therefore **never rationed**, so shaping `Camera3` that way to "make it
free" in fact made it the most expensive of the four — 268 of the 516.

#### The half that is not the SDK's

`DepthLandscapeVisualizer` is the worked exemplar, embedded and selected by a 3D brief, and it clears
the whole ladder including the zero-sized frame. It exists because **gestures and verbs both shipped
documented and undemonstrated and did not transfer** — measured, twice. It is also the only exemplar
that **composes a picture out of primitives** rather than calling a widget, which is brief item 4.

Exactly one exemplar is ever sent, so it costs nothing on a brief that does not ask for it — and a
test pins the expensive direction, that an ordinary book brief still gets the order-flow example.

**What projection cannot do** is occlusion between interpenetrating shapes; painter's algorithm sorts
those wrongly and no ordering fixes it. Exact for scattered markers and for a height field walked back
to front, which is what these pictures are. Said in the doc comment rather than left to be discovered.

#### And the drawing pack was telling the model not to animate

Found while making room for the 3D teaching. *"Reaching for context, market data or **the clock**
inside `Draw` means the work is in the wrong place."* — true when it was written, and flatly wrong
since `surface.Now` shipped the iteration before. A model reading it would decline to animate.

The same shape as every other defect in this file: a capability added, and a document left telling
the reader not to use it. The sentence now names the three reads that are *designed* for `Draw` —
`Viewport`, `Cursor`, `Now` — and says why they are reads.

#### What the whole iteration cost the prompt

| | order-book brief |
|---:|---:|
| before `surface.Now` | 109,247 |
| after animation (`Now` 1,914 unconditional; the exemplar's fade demo 2,883) | 114,253 |
| after 3D (types 516 on this brief; the drawing pack's 3D section, net of two paragraphs cut) | **115,655** |

The skill ceiling moved 20,000 → 21,000 to keep the three heaviest packs fitting together, which is
the second time it has moved for that reason and is recorded where the constant is.
**The exemplar, not the SDK, was the biggest single line in that table** — 2,883 characters for one
demonstration, more than `Now` and the whole 3D library put together.

**Not yet measured: whether a model uses any of it.** That needs a live 3D run, and is the next
benchmark.

### ~~10. No online learners~~ — closed 2026-09-01

The last named item in the brief's maths gap: *"missing from the maths library: online learners, which
the OrderBook window needed 527 hand-written lines of."*

They were not missing. They were **duplicated byte-for-byte across two tool projects**
(`TradingTerminal.OrderBook` and `TradingTerminal.VolumeFootprint`), under the namespace
`TradingTerminal.Core.Ml` — which is neither of the assemblies they compiled into — and reachable by
nothing an author could write. Eight identical files, and **no tests at all**.

Now in `DaxAlgo.Sdk.Quant`, once: `OnlineLinearRegression` (RLS with forgetting),
`OnlineGradientDescent` (ridge SGD), `OnlineLogisticRegression`, `OnlineFeatureScaler` (Welford with
decay), `RollingForecastMetrics` and `RollingBrierScore`. Both windows now use the one copy.

`OnlineLearnerTests` is ten properties rather than a characterisation: RLS converges on a noiseless
target, forgetting tracks a regime change *better than not forgetting*, logistic stays in [0, 1] and
does not overflow its sigmoid at an extreme score, SGD and RLS agree, the scaler leaves the bias
alone, state round-trips, a restore refuses another learner's state, a perfect Brier is 0 and a
coin-flip is 0.25.

One of them was written wrong first and is worth keeping in mind: the RLS test asserted a fixed
tolerance, and failed. `initialDiagonal` is a finite prior covariance, so RLS is a very lightly ridged
fit whose penalty decays with data — a real residual of about 1e-5 after 600 noiseless samples, and
the algorithm behaving correctly. The test now asserts **convergence**, plus exactness under a diffuse
prior, which is what that constructor argument is *for*.

| Cost | |
|---|---:|
| generated surface | 89,830 → 96,109 (+6,279) |
| composed prompt, order-book brief | 115,655 → **116,115 (+460)** |
| the eight types on that brief | 1,638 |

The rationing did the rest: the surface grew by 6,279 and the prompt by 460, because the cut absorbed
it (18,981 → 24,214 saved).

The teaching went into a **new pack** rather than into `quant-math`, which had grown to two topics.
That was not tidiness — adding it to `quant-math` put the three heaviest packs over the ceiling, and
the ceiling had already moved once this session for 3D. A pack that only loads for a brief about
prediction is both better targeted and free to everyone else, and it put the three heaviest back under
budget without raising anything.

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
| after verbs + the exemplar demonstrating them | 99,070 |

The current figures are in entry 9 above (Standard effort, so not directly comparable to these Deep
ones — but the *shape* is what matters and it has not changed): capability keeps spending what the cut
bought.

**The exemplar cannot usefully be SHRUNK — measured — and that is not the same as it being cheap to
GROW.** It is unrationed and 72% code: XML docs are 15.6% and inline comments 12.0%, so stripping every
comment would save 3.7 KB and cost the commenting style the exemplar exists to teach. No cheap win
there.

But the 2026-09-01 measurement put the other half of that on the record. Adding **one** demonstration
to the order-flow exemplar — the sweep fade, about forty lines — cost **2,883 characters**, more than
`surface.Now` and the entire 3D library together. An exemplar is sent verbatim and never rationed, so
it is simultaneously the most effective place to teach something and the most expensive. **Demonstrate
in the exemplar the brief will actually select**, and put a capability that needs its own worked
example in its own exemplar, where only a brief that asks pays for it.

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
