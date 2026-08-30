# Engineering requirements — strategy / visualizer windows

**Audience:** engineers co-working on Hyperion UI generation and the authored-unit window stack.  
**Status:** living requirements, grounded in `origin/main` as of 2026-08-31 (`84cf381`) and GitHub epics [#42](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/42), [#43](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/43), [#44](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/44), [#34](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/34).  
**Product shell:** `TradingTerminal.App.Basic` only (this public repo).

---

## 0. One-sentence product requirement

Hyperion (and any human author) must be able to ship a **strategy or visualizer that opens a real live window** in Basic: parameters on top, an authored picture in the middle, activity log (and virtual book for strategies) below — **without** the host composing panels from a fixed `DataRequirement` map, and **without** mounting arbitrary author WPF.

---

## 1. Problem this work solves

### 1.1 Catalog gap (why users see “nothing”)

Today’s failure mode (documented on #42):

1. Hyperion generates math + view-model.
2. Compile & Register succeeds.
3. Catalog never shows the unit **unless** it ships a live window.
4. Older prompts told the model: *“Do NOT write a view; the host composes from `DataRequirement`.”*
5. Basic **does not** call `AddStrategyViewComposer()` (July 2026 tiering). Backtest fallback was archived 2026-08-17.
6. Result: success toast, empty catalog.

**Acceptance:** a default Hyperion strategy/visualizer that compiles must appear in the catalog and open a window that paints.

### 1.2 What must not be rebuilt

| Do not do | Why |
|-----------|-----|
| Wire `TradingTerminal.StrategyComposer` into Basic | Explicitly superseded by #42/#43; fixed `DataRequirement` → panel map is throwaway |
| Restore standalone Charts / Tools menus | Removed; windows live **inside** the unit |
| Hand Hyperion a free-form WPF/XAML toolkit | Breaks sandbox; forks sealed vs Roslyn render paths |
| Invent a second window chrome for visualizers vs strategies | One anatomy; `HasBook` is the only structural difference |

---

## 2. Architectural decision (mandatory)

### 2.1 Draw-op stream, not WPF controls

**Decision (settled on #43):** an authored unit describes each frame through `IRenderSurface` (immediate-mode draw ops). The host owns pixels.

| Old idea (mood-board / early #42 wording) | Current law on `main` |
|-------------------------------------------|------------------------|
| Named WPF controls in `DaxAlgo.Sdk.Wpf` (`DaxPriceChart`, …) | **Retired path.** `IWpfVisualizer` is `[Obsolete]` — never implemented |
| Host mounts author `FrameworkElement` | Forbidden — defeats sandbox |
| Host maps `DataRequirement` → Charts/OrderBook/Footprint panels | Superseded |
| Author calls `Candles.Draw` / `Ladder.Draw` / `Footprint.Draw` / `Tape.Draw` / … | **Correct** — SDK drawing helpers over `IRenderSurface` |
| Multi-panel via nested WPF grids authored by Hyperion | **Correct substitute:** `UnitLayout` (`PanelNode` / `SplitNode`) — data + delegates, no WPF types |

### 2.2 One window anatomy for both kinds

`AuthoredUnitPresenter` + `AuthoredUnitView` / `AuthoredUnitHost`:

```text
┌─────────────────────────────────────────────────────────────┐
│ Parameter expander (top) — only if Schema declares params   │
├─────────────────────────────────────────────────────────────┤
│ Window BODY = UnitLayout                                    │
│   default: UnitLayout.Single → unit.Draw(IRenderSurface)    │
│   or: rows/columns of PanelNode each with its own Draw      │
├─────────────────────────────────────────────────────────────┤
│ Virtual book row — strategies only (HasBook = true)         │
├─────────────────────────────────────────────────────────────┤
│ Activity log (bottom) — host-owned slice of app log         │
└─────────────────────────────────────────────────────────────┘
```

Rules:

- Author draws **only** the body.
- Chrome is host-owned; Hyperion must **not** generate parameter expander / log / book chrome.
- Visualizers usually have no parameters → expander hidden (`HasParameters` keyed off declared params, not kind).
- Frame pacing is host-owned (`AuthoredUnitHost` coalescing timer). `Draw` must be **pure and fast**; may run more than once per frame; must not block waiting on the data pump.

### 2.3 Sandbox / marketplace consequence

Same primitives for:

- Roslyn-compiled `IVisualizer` / `IStrategyKernel`
- Future sealed / DAXQ path (#43 Phase 5 — deferred)

Bounded render: ≤ **20,000** primitives per frame (truncate, don’t trust). Layout tree bounded: ≤ **16** panels, depth ≤ **6** (`UnitLayout.MaximumPanels` / `MaximumDepth`). Bad layout → fall back to `Single`, never throw.

---

## 3. Current codebase inventory (do not redo)

Verified on tip `84cf381` (update this section when landing PRs).

### 3.1 SDK contracts — done

| Piece | Location | Notes |
|-------|----------|-------|
| `IVisualizer` + `Draw` + `Layout` | `DaxAlgo.Sdk/IVisualizer.cs` | Data callbacks compute; `Draw` paints |
| `IStrategyKernel` | `DaxAlgo.Sdk` | Strategy = visualizer that can trade via `IVirtualBook` |
| `IRenderSurface` + theme/cursor/viewport | `DaxAlgo.Sdk/IRenderSurface.cs` | Immediate mode |
| `NullRenderSurface` / `RecordingRenderSurface` | SDK | Headless + tests |
| Drawing helpers | `DaxAlgo.Sdk/Drawing/*` | See §4 vocabulary |
| `UnitLayout` / `Layout.*` factories | `DaxAlgo.Sdk/Layout/` | Multi-panel body |
| `IWpfVisualizer` | `DaxAlgo.Sdk.Wpf` | Obsolete façade only |

### 3.2 Host render / window — done

| Piece | Location | Notes |
|-------|----------|-------|
| `RenderSurfaceView` + `DrawingContextSurface` | `TradingTerminal.UI/Controls/Render/` | WPF immediate-mode consumer |
| `AuthoredUnitPresenter` / `View` / `Host` / `LayoutHost` | same | Chrome + body |
| `IVisualizerRegistry` + descriptor discovery | Infrastructure / UI | Factory paired with descriptor |
| “Add to chart” opens runtime | Wired (#43 Phase 3) | Dialog if no runtime |
| `SandboxVisualizerRuntime` pump + `TryDraw` gate | Sandbox | Non-blocking frame skip under contention |

### 3.3 Hyperion / codegen — partially done

| Piece | Status |
|-------|--------|
| `AuthoringKind` strategy vs visualizer context packs | Done (#43 Phase 4) |
| Compiler discovery of `IVisualizer` | Done |
| Verification ladder (compile / policy / shape / lifecycle / draw / replay) | In progress per #44 — treat as engineering target |
| Prompt still mentioning host `DataRequirement` composition in places | **Must be scrubbed** wherever it still appears (generated surface still mentions old composer in places) |
| Package **install** `.daxalgostrategy` / `.daxalgovisualizer` | **Blocked on #34** |

### 3.4 Legacy host panels — base layer only

`TradingTerminal.Charts` / `OrderBook` / `VolumeFootprint` / `StrategyComposer`:

- Still in solution; **not** in Basic build closure.
- No tests.
- Reference implementations / benchmarks for *pictures*, not the composition path.
- Acceptance benchmarks (#43): strategy windows ≈ ImbalanceHeatFront / SigmaIcFlow; visualizer pictures ≈ order book + volume footprint.

---

## 4. Public drawing vocabulary (what Hyperion must compose)

Mood-board names from `docs/images/visualizer-refs/` mapped to **shipped** SDK helpers. Hyperion prompts and exemplars must use the **right-hand** column.

| Mood-board / old “control” name | SDK API to call inside `Draw` / panel `Draw` | Primary data |
|---------------------------------|-----------------------------------------------|--------------|
| `DaxPriceChart` | `Candles.Draw`, `Series` / `Plot`, `Bands.Draw` | `Bars` (+ optional overlays) |
| `DaxOscillatorPane` | Separate `PanelNode` or `PlotArea` split + `Series.Draw` (RSI/MACD computed by author) | Derived series |
| `DaxVolumeProfile` | `VolumeProfile.Draw` | Bars / prints as required by helper |
| `DaxOrderBook` | `Ladder.Draw` | `Depth` |
| Dual book + arb strip | `UnitLayout` columns: two `Ladder.Draw` + middle `PanelNode` for spread/state | Multi-instrument `Depth` |
| `DaxFootprint` | `Footprint.Draw` | Footprint bars / tape-derived |
| `DaxTape` | `Tape.Draw` | `TradeTape` |
| `DaxQuoteStrip` | Small `PanelNode` or header tiles via `Tiles` / text primitives | `L1` |
| Parameter expander | **Host chrome** — declare `StrategyParameterSchema` only | — |
| Activity log | **Host chrome** — `IAlertSink` / runtime log | — |
| Virtual book panel | **Host chrome** when `HasBook` | `IVirtualBook` |

Additional helpers already in SDK (teach when relevant): `Heatmap`, `Histogram`, `DepthCurve`, `Equity`, `Gauge`, `Table`, `Signals`, `Levels`, `Legend`, `ColorScale`, `PlotArea` splits inside one panel.

**Compatibility commitment:** anything published under `DaxAlgo.Sdk.Drawing` is a public surface. Prefer extending helpers over teaching Hyperion raw `Line`/`Rect` soup for standard pictures.

---

## 5. Engineering requirements by layer

### 5.1 SDK (`DaxAlgo.Sdk`) — requirements

| ID | Requirement | Priority | Done? |
|----|-------------|----------|-------|
| SDK-1 | `Draw(IRenderSurface)` remains the only render contract for authors | P0 | Yes |
| SDK-2 | Keep `IWpfVisualizer` obsolete until next major; no new implementors | P0 | Yes |
| SDK-3 | Drawing helpers must use `Default` statics that are **not** all-zero record pitfalls | P0 | Yes (pinned by tests) |
| SDK-4 | `UnitLayout.Of` never throws; oversize/malformed → `Single` | P0 | Yes |
| SDK-5 | Theme roles only in helpers by default (no hard-coded RGB that vanishes on themes) | P1 | Yes |
| SDK-6 | Document every new helper in generated `sdk/ai-context/generated/sdk-surface.md` | P0 | Process |
| SDK-7 | Headless testability via `RecordingRenderSurface` for every helper | P1 | Partial |

### 5.2 Host UI (`TradingTerminal.UI` + Basic shell) — requirements

| ID | Requirement | Priority | Done? |
|----|-------------|----------|-------|
| HOST-1 | Every registered strategy/visualizer opens through `AuthoredUnit*` path in Basic | P0 | Mostly |
| HOST-2 | Catalog card appears when unit is hostable (descriptor + factory), not only when custom VM view exists | P0 | Verify on Basic |
| HOST-3 | Parameter expander / log / book never authored by Hyperion | P0 | Yes by design |
| HOST-4 | Frame skip under pump contention; UI thread never blocked waiting on author data handlers | P0 | Yes (`TryDraw`) |
| HOST-5 | Primitive cap enforced; throwing author leaves partial frame, not process kill | P0 | Yes |
| HOST-6 | Do not register `IAuthoredStrategyViewComposer` / StrategyComposer in Basic | P0 | Keep |
| HOST-7 | Multi-panel layouts render with draggable splitters matching `SplitNode` | P1 | Landed with layout host |
| HOST-8 | Empty `Draw` shows intentional empty surface, not a crash or “broken” chrome | P1 | Yes |

### 5.3 Hyperion / Codegen — requirements

| ID | Requirement | Priority | Done? |
|----|-------------|----------|-------|
| HYP-1 | Default path: emit `IStrategyKernel` or `IVisualizer` that implements `Draw` and optional `Layout` | P0 | In progress |
| HYP-2 | Prompt **must not** say host composes from `DataRequirement` | P0 | Audit & fix leftovers |
| HYP-3 | `DataRequirement` remains **subscription** flags only (L1/Bars/Depth/TradeTape), not UI layout | P0 | Teach explicitly |
| HYP-4 | Split agents: Coder (math/lifecycle) vs Painter (`Draw` / layout) per #44 | P1 | Design on #44 |
| HYP-5 | Verification ladder rungs 1–8 before user sees success (#44 §3.1) | P0 | Partial |
| HYP-6 | Draw probe: recording surface, primitive count, no NaN, degenerate viewport survival | P0 | Target |
| HYP-7 | Replay probe: real stored bars/trades/depth + `RecordingVirtualBook` | P1 | Target |
| HYP-8 | Verified exemplars compile in CI (ImbalanceHeatFront, SigmaIcFlow, OrderBook, VolumeFootprint) | P0 | Target |
| HYP-9 | Kind isolation: strategy vs visualizer context packs; kind mismatch → ordinary compile-fix loop | P0 | Done |
| HYP-10 | Budget / token caps visible; fail with partial artifact rather than infinite repair loops | P1 | #44 |

### 5.4 Packaging / marketplace — requirements

| ID | Requirement | Priority | Done? |
|----|-------------|----------|-------|
| PKG-1 | Extensions can install `.daxalgostrategy` / `.daxalgovisualizer` (#34) | P0 | **Blocked** |
| PKG-2 | Installed package registers via same registry/factory path as Hyperion compile-in-process | P0 | Depends PKG-1 |
| PKG-3 | Sealed DAXQ render host parity with `IRenderSurface` renderer (#43 Phase 5) | P2 | Deferred by owner |
| PKG-4 | No third package format (no `.agentpack`) | P0 | Policy |

### 5.5 Tests — requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| TEST-1 | Unit tests for each Drawing helper default path (non-zero defaults) | P0 |
| TEST-2 | `AuthoredUnit*` tests: params visibility, book row, log trim, layout fallback | P0 |
| TEST-3 | Recording-surface golden or structural asserts for OrderBook + Footprint exemplars | P0 |
| TEST-4 | Hyperion verification ladder integration tests with failing rungs → Fixer diagnostics | P1 |
| TEST-5 | Basic shell smoke: compile → register → catalog → open → non-empty frame on live/keyless feed | P0 |

Run tests with `dotnet test TradingTerminal.Windows.slnx -m:1`.

---

## 6. Functional scenarios (acceptance tests in prose)

### S1 — Single-chart strategy (minimum happy path)

**Given** Hyperion brief: “EMA crossover on one instrument, show candles + EMAs.”  
**Then** generated unit:

- Declares `Bars` (+ `L1` if needed).
- Implements `Draw` using `Candles` + series overlays.
- Leaves `Layout` as default `Single`.
- Declares parameters (instrument, fast/slow).
- Compiles, registers, **appears in catalog**, Open shows expander + picture + book + log.
- On bars, surface receives candle/series primitives (draw probe passes).

### S2 — Order-book visualizer

**Given** “Show L2 ladder.”  
**Then** `IVisualizer`, `Depth` requirement, `Ladder.Draw` in `Draw`, no book row, Add to chart opens painting ladder.

### S3 — Dual-book arbitrage window (#42 example)

**Given** two instruments + spread.  
**Then** `Layout` columns: book | strip | book (or rows). Strip panel draws spread/state text or series. Depth for both instruments authorized. Layout ≤ bounds.

### S4 — Footprint + tape

**Given** tape+footprint brief.  
**Then** `TradeTape` declared; `Footprint.Draw` and/or `Tape.Draw`; cursor/crosshair may use `RenderCursor` without host knowing hover semantics.

### S5 — Refusal / safety

**Given** author attempts WPF `UserControl` or P/Invoke.  
**Then** analyzer/policy fails compile; never registers.

### S6 — Pathological layout

**Given** generated tree with 100 panels.  
**Then** `UnitLayout.Of` → `Single`; window still opens; diagnostics tell author layout was refused.

---

## 7. Non-goals (explicit)

1. Rebuilding MT5-style standalone chart application windows.
2. Teaching Hyperion XAML.
3. Making `StrategyComposer` the Basic default again.
4. Claiming visualizer **install** works before #34.
5. Shipping a learned agent controller / ONNX router (#44 drops this).
6. Day-one sealed-format DAXQ parity (#43 Phase 5 deferred).
7. Treating mood-board PNGs as shipped UI.

---

## 8. Sequencing for co-work (recommended)

Work only against **current `main`** (pull first; local WIP may be weeks behind).

| Phase | Owner focus | Exit criteria |
|-------|-------------|---------------|
| **A. Sync & audit** | Both | On tip of `origin/main`; list every prompt/doc still saying “host composes from DataRequirement” |
| **B. Prompt + exemplar truth** | Hyperion | Skills `drawing.md` / `layout.md` authoritative; exemplars CI-green |
| **C. Verification ladder** | Codegen (#44) | Rungs 5–8 implemented; failing draw cannot report success |
| **D. Catalog/open smoke on Basic** | Shell | S1 manual + automated smoke green on keyless crypto |
| **E. Multi-panel Hyperion** | Painter agent | S3 generated without hand-written layout from human |
| **F. Package install** | #34 | Generated unit → `.daxalgo*` → install → open |

Do **not** start a parallel “Dax* WPF control library” — that forks the settled render design.

---

## 9. Engineering environment requirements

| Requirement | Detail |
|-------------|--------|
| OS | Windows 10/11 |
| SDK | .NET 9 |
| Clone | `https://github.com/dhruuvsharma/DaxAlgo-Terminal.git` |
| Build | `dotnet build TradingTerminal.Windows.slnx` |
| Run Basic | `dotnet run --project src/windows/Shell/TradingTerminal.App.Basic` |
| Tests | `dotnet test TradingTerminal.Windows.slnx -m:1` |
| Build output root | `C:\DaxAlgoBuild` by default (`DaxAlgoBuildRoot`) |
| Brokers for smoke | Keyless crypto OK (Binance/Coinbase/Bybit/Kraken/OKX); no account |
| Credentials | `appsettings.local.json` git-ignored only |
| AI providers | Settings › AI providers; keys never committed |

---

## 10. Doc & issue map

| Doc / issue | Use for |
|-------------|---------|
| This file | Engineering requirements (normative for co-work) |
| [#42](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/42) | Product epic: Hyperion builds the window |
| [#43](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/43) | Render contract, renderer, registry (mostly landed) |
| [#44](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/44) | Agentic Hyperion + verification ladder |
| [#34](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/34) | Package install blocker |
| `sdk/ai-context/skills/drawing.md` | How to paint |
| `sdk/ai-context/skills/layout.md` | Multi-panel |
| `sdk/ai-context/generated/sdk-surface.md` | Generated API truth |
| `docs/images/visualizer-refs/README.md` | Mood-board index (not shipped UI) |
| Root `README.md` | Product/build; points catalog gap at #42 |

---

## 11. Definition of done (co-work slice)

This requirements track is **done** when all are true:

1. Default Hyperion strategy on Basic: compile → catalog → Open → living picture on real or keyless data.
2. Default Hyperion visualizer: Add to chart → living picture.
3. No prompt or skill tells authors the host will compose from `DataRequirement`.
4. Multi-panel layouts only via `UnitLayout` + Drawing helpers.
5. Verification rejects empty/NaN/over-budget draws before success UI.
6. Eng docs (#42/#43/#44 cross-links) match code; mood-board explicitly non-normative.
7. #34 either done or explicitly scheduled as the remaining delivery gate for “shipped file.”

---

## 12. Changelog of understanding (for newcomers)

| Date | Understanding |
|------|----------------|
| Early #42 / visualizer-refs | “Ship named WPF SDK controls Hyperion snaps together.” |
| #43 decision | “Ship draw-op surface + host chrome; helpers replace named controls.” |
| `main` now | Helpers + `AuthoredUnit*` + registry largely present; Hyperion verification + prompt scrub + #34 remain the real eng work. |

If a design doc still says “implement `DaxPriceChart` as a WPF control,” treat it as **historical** and translate to `Candles.Draw` / `UnitLayout` before writing code.
