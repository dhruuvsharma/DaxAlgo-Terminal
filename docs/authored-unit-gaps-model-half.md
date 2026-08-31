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
transcript, the generated source, and a summary table. `HYPERION_LIVE_MODEL` picks the model.

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

### Still unreached, found while fixing the above

`DrawProbe.RunDegenerate` — the second pass against a zero-sized viewport, which the file itself
motivates with "a panel collapsed to nothing, a window restored minimised, a layout pass before
measurement" — **is called by no verifier**. Only by tests. So a unit that divides by the viewport
still reaches the render thread of a running application. Left open deliberately: adding it makes the
ladder stricter, which changes which generated units pass, and that deserves its own measurement rather
than being smuggled into this one.

## The delta table

Kept so the next run has something to move.

| | 2026-09-01, run 1 |
|---|---|
| compiles | ✓ (2 generations) |
| clears the ladder | ✓ (after the verifier fix; ✗ before it, wrongly) |
| the three panels of the brief | ✓ |
| microstructure maths from the library | ✓ |
| verbs + take-away | ✓ |
| gestures (pin / zoom / pan / scroll) | ✗ — none |
| crosshair + hover readout | ✗ |
| a picture per statistic | partial — tiles, no imbalance lane |
| `Draw` fallback well-formed | ✗ — no panel scope |
| asked the user anything | ✗ — built directly (the brief was specific) |
