# Hyperion visualizer / window-control refs

Mood-board only. These images are **original mockups** (not vendor screenshots).

**Normative engineering requirements:**  
[`docs/ENGINEERING_REQUIREMENTS_STRATEGY_WINDOWS.md`](../../ENGINEERING_REQUIREMENTS_STRATEGY_WINDOWS.md)

Do **not** treat a mockup as a shipped WPF control. On current `main`, authors paint through
`IVisualizer.Draw(IRenderSurface)` and `DaxAlgo.Sdk.Drawing.*` helpers. `IWpfVisualizer` is obsolete.
Multi-panel windows use `UnitLayout`, not nested author XAML. Host chrome (parameter expander,
activity log, virtual book) is owned by `AuthoredUnitPresenter` — Hyperion must not generate it.

Tracked on [#42](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/42) /
[#43](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/43).

## What already ships (do not redo)

| Piece | Status |
|---|---|
| `IVisualizer` + `Draw` + `Layout` | SDK — render contract |
| `IRenderSurface` + Drawing helpers | `Candles`, `Ladder`, `Footprint`, `Tape`, `VolumeProfile`, … |
| Host window chrome | `AuthoredUnitPresenter` / `AuthoredUnitHost` |
| `IVisualizerRegistry` + Add to chart path | Landed (#43 Phase 3) |
| Hyperion kind packs | Strategy vs visualizer (#43 Phase 4) |
| `IWpfVisualizer` | **Obsolete** — do not implement |
| Host `StrategyComposer` map | **Superseded** — do not wire into Basic |
| `.daxalgovisualizer` install | Still blocked on [#34](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/34) |

## Mood-board → SDK mapping

| Image | Old nickname | Call in `Draw` / panel |
|---|---|---|
| `chart-candles-ema-bands.png` | `DaxPriceChart` | `Candles.Draw` + `Bands` / series |
| `chart-oscillators-rsi-macd.png` | `DaxOscillatorPane` | Extra `PanelNode` + `Series` |
| `volume-profile.png` | `DaxVolumeProfile` | `VolumeProfile.Draw` |
| `order-book-ladder.png` | `DaxOrderBook` | `Ladder.Draw` |
| `dual-book-arb.png` | dual book + strip | `UnitLayout` columns + two ladders |
| `volume-footprint.png` | `DaxFootprint` | `Footprint.Draw` |
| `tape-prints.png` | `DaxTape` | `Tape.Draw` |
| `quote-strip.png` | `DaxQuoteStrip` | `Tiles` / text primitives |
| `parameter-expander.png` | chrome | Host — declare `Schema` only |
| `activity-log.png` | chrome | Host |
| `virtual-book-panel.png` | chrome | Host when strategy `HasBook` |
| `strategy-window-chrome.png` | full window | Host + body `Draw` |

PNG binaries for this mood-board may live locally beside this README; they are optional for the
requirements track. Prefer the mapping table above over treating filenames as an API.

## Next eng slice (see full requirements doc)

1. Scrub prompts that still say the host composes from `DataRequirement`.
2. Verification ladder draw/replay probes (#44).
3. CI exemplars: OrderBook + Footprint + benchmark strategies.
4. Basic smoke: compile → catalog → open → living frame.
5. Package install (#34).
