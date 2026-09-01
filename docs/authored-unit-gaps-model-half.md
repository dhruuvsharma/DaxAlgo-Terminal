# The model half of the benchmark

`authored-unit-gaps.md` measures the **contract**: what a hand-written authored unit cannot express,
using `samples/DaxAlgo.Sandbox.Samples/LiquidityBookVisualizer.cs` as a control written with full
knowledge of the SDK and no model involved. Everything there is missing from the SDK, and a model
cannot be marked down for any of it.

This file is the other half: **what a model actually produced, on the same brief, against the same
window.** Kept separate on purpose — a shortfall that belongs to the contract and a shortfall that
belongs to the model need different work, and merging them makes both unattributable.

## How to reproduce it

`HyperionBenchmark` (in `tests/TradingTerminal.Plugins.Tests`) is the drive, committed so a number can
be compared to the next one rather than remembered.

```powershell
# offline — runs on every build, and exercises the whole driver against a canned reply
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~HyperionBenchmarkTests

# live — spends tokens
$env:OPENROUTER_API_KEY = "…"; $env:HYPERION_LIVE = "1"
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~A_model_answers

# re-judge a unit a previous run produced, without paying for another generation
$env:HYPERION_REVERIFY = "…\artifacts\hyperion-benchmark\<run>\BookHeatmapVisualizer.cs"
dotnet test tests/TradingTerminal.Plugins.Tests --filter FullyQualifiedName~Reverify_a_saved_unit
```

Artifacts land in `artifacts/hyperion-benchmark/<utc>-<label>/`: the composed system prompt, the full
transcript, the generated source, and a summary table. The provider comes from the environment —
`HYPERION_LIVE_KEY`, `HYPERION_LIVE_BASE_URL`, `HYPERION_LIVE_MODEL`, `HYPERION_LIVE_PROVIDER` —
because the useful free model changes month to month and a benchmark hard-wired to one vendor stops
being run the day that vendor stops being the answer. The defaults are the openrouter free tier.

**The artifacts land under the build output**, not the repo: the tests run from a redirected `bin`, so
the repo-root walk that looks for `TradingTerminal.Windows.slnx` does not find one. Look under
`…\bin\Debug\net9.0-windows7.0\artifacts\hyperion-benchmark\`.

**The offline and live runs differ by one constructor argument.** That is the point: the driver — the
composition, the escape reply, the compile, the ladder, the artifact writing — is exercised on every
build, so it cannot rot between the live runs that are months apart.

## Run 1 — 2026-09-01

| | |
|---|---|
| provider | openrouter, `minimax/minimax-m3:free` |
| why that model | the account is **free tier**, so a paid model returns HTTP 402. This is the weakest realistic case, deliberately |
| kind / effort | Visualizer / Standard (single conversation, 3 skill packs, 2 fix attempts) |
| brief | *"An order book window: the depth ladder, a liquidity heatmap over time, and the microstructure statistics."* |
| system prompt | 109,247 chars, of which the surface cut saved 17,332 |
| skills selected from the brief | order flow · drawing · quant math |
| turns | **1 — it did not interview, it built** |
| generations | 2 (one auto-fix) |
| wall clock | 210.4 s |
| tokens | 57,879 in / 47,223 out |
| output | `BookHeatmapVisualizer.cs`, **441 lines / 19,472 chars** |
| ladder | Lifecycle ✓ · SchemaCoherence ✓ · DrawProbe ✓ · Replay n/a |

For scale: the control is 439 lines and the hand-written `TradingTerminal.OrderBook` is a 1,154-line
view-model plus 492 lines of XAML plus 448 of code-behind.

**It did not ask anything.** The previous run (2026-08-31, recorded in the loop brief) asked three
well-formed questions on a vaguer brief. This brief names its three elements, so there was less to
settle — which is the interview working, not failing, but it means the questioning path was not
exercised here and the "Just build it" escape was never sent.

## What it got right

Nothing in this list was prompted for beyond the one line above.

- **The arrangement**, matching the control almost exactly: heatmap dominant (`Star(5)`), ladder as a
  fixed 240px column beside it, statistics as an 80px strip beneath. Declared as a `UnitLayout` with
  one callback per panel.
- **Seven parameters**, grouped (Market / Book / Heatmap / Flow), each with min, max, step and unit.
- **Both verbs, and the take-away.** `Reset heatmap` and `Copy book`, the latter through
  `context.Export.Offer(…)` — inside `OnActionAsync`, which is the only place an offer is honoured.
- **All three data callbacks**: depth drives the heatmap and the ladder, quotes sign the prints and
  feed the spread statistic, prints feed order-flow imbalance and VPIN.
- **The maths from the library, not by hand**: `Book.Microprice`, `Book.Imbalance`,
  `Book.SweepSlippage` (both sides), `TradeClassifier`, `OrderFlowImbalance`, `Vpin`, `SpreadStats`.
- **Bounded memory**: a ring of heatmap columns, each rebased to the current mid at draw time rather
  than at capture, so a price move does not push history out of view.
- Two things the control does *not* do: it exposes the VPIN bucket size as a parameter rather than
  hard-coding 500, and it infers the instrument's tick size from the first two-level snapshot, with a
  comment saying why (the host does not surface one).

## What it missed — the model's half

### 1. It used no gestures at all

Zero references to `Cursor`, `Viewport` or `Crosshair`. The control uses four: a pinned price row from
`Cursor.HasSelection`, `Viewport.Zoom` and `PanX` to choose the visible window, and
`LadderOptions.FirstLevel` driven from `PanY` to scroll the book.

And it did the thing the gesture work exists to remove: **`heatmapColumns` is a parameter**. That is
precisely the workaround the control deleted on 2026-08-31 once zoom existed — "how much history do I
want" answered by typing a number into a form rather than by turning a wheel over the picture.

**Diagnosis: teaching, not contract.** The capability shipped, it is documented in the surface, and the
exemplar `BookPressureVisualizer` demonstrates it. It still did not transfer. The most likely reason is
weight: gestures are three lines in a 300-line exemplar and one entry among many in the surface, while
the *shape* of a unit — schema, callbacks, layout, draw — is the whole exemplar and was copied
faithfully. **A model imitates the exemplar far more strongly than it reads the reference**, which the
loop brief already states as measured, and this is another instance of it.

### 2. No crosshair and no hover readout

Related to the above and worth listing separately, because it needs no gesture *state* — `RenderCursor`
is a read and `RenderSurfaceView` invalidates on `MouseMove`, so a crosshair costs one call. The control
draws price and size at the pointer over the heatmap.

### 3. No signed-imbalance lane

The control draws a `Histogram` of signed imbalance under the heatmap; the model drew no fourth picture.
Minor — the same number is in the strip as a tile — but it is one less thing to read at a glance.

### 4. Its `Draw` fallback does not open a panel

Both exemplars open `using var panel = surface.Panel(…)` in `Draw` and do **not** in their layout
callbacks, because the layout host owns the panel region and header. The generated unit copied the
second half and not the first, so its single-surface fallback draws unclipped and untitled.

Invisible in the real window, which uses the layout. Visible in the authoring **preview**, which does
not.

## What this run found in the HARNESS, not the model

The first verdict on this unit was **DrawProbe failed: `draw.no-panel`**. That was wrong, and chasing
it found a defect worth more than the run.

`AuthoredUnitVerifier` drove `unit.Draw` and nothing else. But a unit that declares a `UnitLayout` is
rendered by `AuthoredUnitLayoutHost`, which builds one surface per panel and binds it to that panel's
own callback — `Draw` is not called at all, and the SDK documentation says so in as many words: *"Draw
is then unused, because the panels do the drawing."*

So rung 7 was judging the one method the host does not use, and never touching the three it does. Wrong
in both directions:

| | |
|---|---|
| A correct unit that declares a layout and leaves `Draw` at its default | **failed** with `draw.blank` |
| A unit whose visible panel throws, emits NaN, or blows the frame budget | **passed**, as long as the unused fallback was fine |

The second direction ships a broken window. The first costs money: `AuthoringJudge` turns a rung
failure into a repair turn, so on the Deep and Max paths a false failure spends a whole generation
asking a model to rewrite working code — the exact thing that class's own doc warns about.

Fixed by `DrawProbe.RunLayout`, which walks the layout and judges every panel, naming findings by panel
("Panel 'Book': …") because *"something in this window draws nothing"* is not actionable. `draw.no-panel`
is asked only of `Draw`, never of a panel callback, since the host already owns that region.

**Re-judged against the corrected ladder, the generated unit clears every applicable rung.** The
shortfalls in the list above are real; the one the ladder reported was not.

This is the ninth defect in this area of the shape *built, unit-tested, never reached on the path that
runs* — and the first where the thing not reached was the **verifier's own subject**. `DrawProbe` has
thirteen tests and every one of them hands it a callback directly.

### ~~Still unreached~~ — measured and wired in, 2026-09-01

`DrawProbe.RunDegenerate` — the second pass against a zero-sized viewport, which the file itself
motivates with "a panel collapsed to nothing, a window restored minimised, a layout pass before
measurement" — **was called by no verifier**. Only by its own tests. So a unit that divides by the
viewport reached the render thread of a running application.

It was left open for one iteration on the grounds that a stricter ladder changes which generated units
pass, and that deserved measuring rather than assuming. **Measured: it changes no verdict on either
exemplar, the control, or either of the two units a live model produced.** It is free to be right
about, so it now runs — per panel, alongside the normal frame, merged into the one rung so no consumer
has to decide which of two `DrawProbe` steps it meant.

## Run 2 — 2026-09-01, a stronger model, and a worse window

| | run 1 | run 2 |
|---|---|---|
| provider | openrouter `minimax/minimax-m3:free` | tokenrouter `z-ai/glm-5.3-free` |
| turns / generations | 1 / **2** (one auto-fix) | 1 / **1** — compiled first try |
| wall clock | 210 s | **1,017 s** |
| tokens in / out | 57,879 / 47,223 | 29,559 / **69,370** |
| output | 441 lines | **677 lines** |
| ladder | **clears every rung** | **fails rung 7** |

The stronger model wrote half again as much code, compiled it on the first attempt without a single
fix, and produced more output tokens than it consumed input — it reasons hard and it shows. It also
produced a window whose **main panel never paints**, and no amount of reading the source would tell
you that.

### The defect, and it is a good one

`Panel 'Liquidity': After data arrived the unit still drew only text.`

The heatmap infers the instrument's tick width from the book, because the host does not expose one —
the same instinct run 1 had, and a good one. But:

```csharp
if (_tickWidth > 0d)                       // ← the gate
{
    if (_running is null || …) { FinalizeColumn(); UpdateTick(); … }   // ← the only caller
    Accumulate(depth);
}
```

`UpdateTick()` is the only thing that ever assigns `_tickWidth`, and it is reachable only from inside
a branch guarded by `_tickWidth > 0d`. **A bootstrap deadlock.** The tick width is never learned, so no
time slice ever closes, so no column is ever accumulated, so the dominant panel of a 677-line window
draws *"Learning the price grid…"* for as long as it is open.

It compiles. It runs. It clears lifecycle and schema coherence. Every statistic in its strip is
correct. Reviewing the source, the gate reads as ordinary defensive programming.

**Attribution checked, not assumed.** The obvious suspicion was the harness — the drive's clock was
frozen, so a unit slicing by wall clock would close no bucket for a reason that is ours. That was a
real gap and it is fixed (below); the unit was then re-judged against the advancing clock and returns
**the same verdict**, because the deadlock does not involve time at all.

**This is the case rung 7 exists for**, and it is only visible because the ladder now judges the
panels the host renders rather than the unused `Draw` fallback. Judged the old way this unit passes.

### And the check that catches it was not running

Rung 7 catches this unit exactly. It was gated behind `StrategyBuildProfile.Verify`, which only **Deep
and Max** set — and this unit was built at **Standard**, the default. So the app would have handed it
over clean, and the only way to find the dead panel was to open the window.

**The ladder spends nothing.** `SmokeAsync` is local: it drives the compiled unit through its own
lifecycle and reads back what it drew. No provider call, no tokens, milliseconds. The effort dial is
documented in its own summary as buying extra *generations* — more skill packs, more auto-fix retries,
a self-review pass, the agent committee — so a free check sat on a dial that means "spend more", and
the cheap settings bought less correctness for no saving at all.

Fixed: `Verify` is true at every effort, and the dial's doc now states the rule that was broken —
everything it carries costs a generation, which is the test for whether something belongs on it.
Nothing blocks; a failed rung has always been a warning on the compile result.

### Two things the run found in the harness

**1. `SyntheticDrive` supplied data but not time — fixed.** Its clock was frozen at the epoch while its
bars marched a minute apart, so a hundred and twenty bars of market data arrived in zero seconds. A
liquidity heatmap slicing every second — the ordinary way to build one — closes no slice in the whole
drive and draws its warm-up message forever, and rung 7 then reports a blank panel for a unit that
would paint perfectly against a real feed. That is the expensive direction: it sends a repair agent to
rewrite working code. The clock now advances with the revealed bars, and stays deterministic because it
is the bar series rather than the machine's clock. Exactly the same omission as feeding no depth and no
tape, one file over and one iteration later.

**2. `AiCodegenOptions.TimeoutSeconds` did not bound a streaming generation — fixed, and it was worse
than it looked.** Observed, not reasoned: `HttpClient.Timeout` was 15 minutes, the generation took 17,
and nothing fired. The streaming path sends with `ResponseHeadersRead`, so the timeout covers the
header phase only.

Chasing it found the thing underneath: **the reader could not be cancelled either.** The loop was
`while (!reader.EndOfStream)`, and `StreamReader.EndOfStream` is a synchronous read that ignores the
token — so the Stop button, which `TimeoutSeconds`' own documentation names as *"the control that
actually belongs here"*, did not stop a provider that had gone quiet. The control the configuration
defers to did not work.

Both fixed. The bound is now an **idle** timeout, reset on every line, because a reasoning model
legitimately emits nothing for minutes (278 s before its first byte, measured) and a total wall clock
would abandon exactly the generations worth waiting for. `StalledStreamTests` covers all four cases,
including the one that matters most: a stream that answers *slowly* is not cut off.

Also worth recording: that provider rate-limits at 8 requests/minute, which the fix loop can exceed on
its own; the first attempt died on a 429 at the first request. The benchmark reported it as a
`ProviderError` and failed loudly, which is right — a provider failure is ours, not the model's.

### What run 2 changes about the conclusions

**First-generation compile success is not the metric.** The model that needed a repair turn produced
the working window; the one that needed none produced a broken one. Anything that scores authoring on
compiles-per-generation would rank these two backwards.

**The run-1 gesture finding has to be narrowed, and that is what a second data point is for.** Run 2
used **all four**: `Viewport.Zoom` and `PanX` choose its visible history, `PanY` feeds
`LadderOptions.FirstLevel` so the book scrolls, and `Cursor.HasSelection` pins a price row. So the
capability does transfer, the documentation and the exemplar are sufficient for a model that is
strong enough, and *"gestures are a teaching gap"* is wrong as stated. What is true is narrower:
**gestures are the first thing to go at the weak end.** They are the part a struggling model drops
while still producing something that compiles and looks complete — which is worth knowing, because it
is invisible in every check except a human opening the window.

## Run 3 — the battlefield, 2026-09-01, and it built one

The first two runs asked for an order book, which is what the widget library is *for*. This one is the
goal's own reference case and asks for a picture the library has never seen: **a scene in 3D, one mark
per resting order, and motion.** Three things at once that no widget provides.

| | |
|---|---|
| brief | *"The order book as a 3D battlefield: each resting order is a soldier standing on the price it rests at, and the armies move as the book changes."* |
| provider / effort | tokenrouter `z-ai/glm-5.3-free`, Standard, visualizer |
| system prompt | 91,720 chars — **smaller than the order-book brief's 115,655**, and that is the finding below |
| turns / generations | 1 / 1, compiled first try |
| wall clock | 1,190 s |
| output | `OrderBookBattlefieldVisualizer.cs`, 542 lines |
| ladder | Lifecycle ✓ · SchemaCoherence ✓ · **DrawProbe ✗ `draw.no-panel`** |

**It built the battlefield.** Thirty references to the projection types that had shipped an hour
earlier: `Camera3.Default`, `Camera3.Orbit` driven from `surface.Now`, `Projection3.Of` sized from the
panel, `Vec3` positions, `InFront` tested before every segment, and

```csharp
items.Sort((x, y) => y.Depth.CompareTo(x.Depth));   // far first, near last
```

— painter's algorithm, unprompted, correct. Ground plane, front lines, soldiers, fallen, standards.
This is the capability the goal is about, and it works.

### Two defects, both mine, both from the same day

**1. The drawing pack — where I had just written the entire 3D teaching — was never selected.** The
run loaded exactly one skill, order-flow. **Not one of the drawing pack's thirty-five triggers appears
in that brief**: no "chart", no "draw", no "picture", no "depth". So the prose explaining projection,
painter's algorithm and `InFront` could not reach the model that needed it.

It built the thing anyway, **from the exemplar alone** — and copied one of its comments *verbatim*.
That is the loop's own lesson confirmed twice over: a model imitates the exemplar far more strongly
than it reads the reference, and a pack a brief cannot select is a pack nobody wrote. Fixed by adding
spatial and scene triggers; `A_brief_that_asks_for_a_picture_in_space_gets_the_drawing_pack` fails
against the old list.

**2. `draw.no-panel` — and it is now three generated units in a row.** The unit's `Draw` computes
`PlotArea.Of(surface)` and draws, without ever opening a panel scope. So did run 1's fallback. The
cause is the *shape of the exemplars*: `Draw` is a one-line wrapper that opens the panel and calls a
private method that does the work, and a model copies the method with the work in it. The 3D exemplar
now opens its panel in the same method as the drawing it guards, with a comment saying why.

### What run 3 changes

- **The library is not the ceiling.** Asked for something no widget provides, a free model composed it
  from primitives rather than substituting or refusing. That was brief item 4's open question.
- **The exemplar is doing nearly all the teaching.** It carried a same-day capability into working code
  with the explanatory pack entirely absent. The corollary is uncomfortable: whatever an exemplar's
  *shape* omits is omitted downstream, which is exactly how `draw.no-panel` happened three times.
- **A harder brief can produce a SMALLER prompt.** 91,720 against 115,655, because fewer packs matched.
  Skill selection is keyword scoring, and the briefs that need the most help are the ones least likely
  to contain the keywords.

## Nothing has ever asked a question, and it is not a wiring fault

The goal says Hyperion should **ask the user about the picture while it builds, iteratively**, because
*"a unique UI cannot be specified in one line and the difference between a good window and a bad one
is mostly questions nobody asked."*

**Three live runs. Zero questions.** Including the battlefield, which is genuinely underspecified —
what does a soldier look like, how many, what happens when an order is cancelled, what marks a fill.

**Checked before theorising, because this area's usual answer is that the instruction never arrived.**
It arrives. Grepped in the saved `system-prompt.md` of every run: *"Ask before you guess"*, *"Ask as
many as the job needs"* and *"A specification awaiting approval IS a question"* are all present, once
each, in the composed prompt. This is the model reading the instruction and declining.

The one run that ever asked — three well-formed questions with options, recorded 2026-08-31 — had a
**vaguer brief and a different model**.

So the honest reading is that the instruction is *balanced*, and a confident model resolves it toward
building: *"The test for whether to ask is whether the answer changes what you write. If you cannot
name the line of code that would differ, do not ask."* That is good advice for a specific brief and
exactly wrong for a picture nobody has seen, where every answer changes a line and the model cannot
know which. The **"specification awaiting approval IS a question"** rule is the weakest lever in the
pack: it asks a model to volunteer a checkpoint it has no incentive to add.

**Not acted on, because one confounded comparison is not evidence.** The two variables that differ
between the run that asked and the three that did not — brief specificity and model — have never been
separated. The experiment that would settle it is one run of a deliberately vague brief on the model
that did not ask. Until then this is an observation with a plausible story attached, which is the kind
of thing this file exists to keep apart from a finding.

## The delta table

Kept so the next run has something to move.

| | run 1 · minimax-m3 | run 2 · glm-5.3 |
|---|---|---|
| compiles | ✓ (2 generations) | ✓ (1 generation) |
| clears the ladder | ✓ (after the verifier fix; ✗ before it, wrongly) | **✗ — its main panel never paints** |
| the three panels of the brief | ✓ | ✓ (two of them work) |
| microstructure maths from the library | ✓ | ✓ |
| verbs + take-away | ✓ | ✓ |
| gestures (pin / zoom / pan / scroll) | ✗ — none | ✓ — all four |
| crosshair + hover readout | ✗ | ✗ |
| a picture per statistic | partial — tiles, no imbalance lane | partial |
| `Draw` fallback well-formed | ✗ — no panel scope | ✓ |
| asked the user anything | ✗ — built directly | ✗ — built directly |

Neither run drew a crosshair or a hover readout, and neither asked a question on this brief. Those are
the two things to watch on run 3.
