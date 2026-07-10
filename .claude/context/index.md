# index — master file index (Windows tree)

Read this + `symbols.md` + `deps.json` FIRST on any change request (see `PROTOCOL.md`).
Per-file rows live in `index/<Group>.md` (one per solution group, ~880 rows total) — **grep them, don't read them whole**:

```
rg -i "footprint" .claude/context/index/        # find a file by name/purpose keyword
rg "\| 9[0-9][0-9] \|| [0-9]{4} \|" index/Core.md   # find big files (LOC column)
```

## Group files

| index/ file | Rows | Covers |
|---|---|---|
| `Core.md` | 241 | TradingTerminal.Core (domain, quant, ML math) |
| `Pipeline.md` | 141 | Infrastructure + MarketData |
| `Shell.md` | 175 | App.Basic + App.Intermediate + Login + UI |
| `Strategies.md` | 81 | all 9 strategy plugins |
| `Charts.md` | 23 | Charts, OrderBook, VolumeFootprint, Heatmap |
| `Tools.md` | 40 | Correlation, Backtest, BacktestStudio, Recording, AdvancedMarketRegime |
| `UI.md` | 32 | UI.Core + Settings |
| `Backtest.md` | 31 | Backtest.Engine |
| `AI.md` | 4 | TradingTerminal.Ai (seam only) |
| `Sdk.md` | 3 | DaxAlgo.Sdk (+ Wpf facade, 0 files) |
| `Samples.md` | 1 | DaxAlgo.SamplePlugin |
| `Tests.md` | 104 | Tests + Tests.Headless |

Columns: `File | LOC | Tree | Project | Ed | Pub | Purpose`. Ed: B=Basic, I=Intermediate, P=Pro. Purpose is auto-extracted from the first `<summary>` (imperfect — trust the filename + symbols over it). Regenerate: `bash .claude/context/gen-context.sh`.

## Per-project rollup (LOC / files)

| Project | LOC | Files | Editions | Notes |
|---|---|---|---|---|
| TradingTerminal.Core | 17,221 | 241 | B I P | biggest; 403 public types |
| TradingTerminal.Infrastructure | 14,855 | 103 | B I P | brokers, engine, plugins, research |
| TradingTerminal.Tests.Headless | 11,248 | 94 | dev | |
| TradingTerminal.MarketData | 5,825 | 38 | B I P | canonical pipeline |
| TradingTerminal.App.Intermediate | 4,562 | 40 | I | shell copy — fixes ×3 |
| TradingTerminal.App.Basic | 4,545 | 40 | B | shell copy — fixes ×3 |
| TradingTerminal.Strategies.SigmaIcFlow | 4,390 | 12 | B I P | engine `ApexScalperStrategy.cs` 1,846 LOC |
| TradingTerminal.UI | 4,045 | 50 | B I P | themes, shared controls |
| TradingTerminal.Login | 3,847 | 45 | B I P | Basic registers keyless forms only |
| TradingTerminal.Strategies.CumulativeDelta | 2,700 | 8 | B I P | VM is 1,355 LOC |
| TradingTerminal.VolumeFootprint | 2,518 | 6 | B I P | chart-ML window |
| TradingTerminal.UI.Core | 2,246 | 21 | B I P | LiveSignalStrategyViewModelBase (940) lives HERE |
| TradingTerminal.OrderBook | 2,088 | 6 | B I P | chart-ML window |
| TradingTerminal.Backtest.Engine | 1,950 | 31 | B I P | |
| TradingTerminal.Heatmap | 1,813 | 7 | B I P | Bookmap+VolBook combined |
| TradingTerminal.Strategies.IndexRegimeGraph | 1,772 | 8 | B I P | |
| TradingTerminal.Correlation | 1,761 | 10 | B I P | |
| TradingTerminal.Strategies.IndexKScoreSurface | 1,721 | 8 | B I P | |
| TradingTerminal.Strategies.OrderFlowSurfaceSpike | 1,650 | 10 | B I P | Helix 3D |
| TradingTerminal.Strategies.OrderFlowCube | 1,502 | 10 | B I P | Helix 3D |
| TradingTerminal.Strategies.ImbalanceHeatFront | 1,391 | 8 | B I P | |
| TradingTerminal.Backtest | 1,358 | 8 | B I P | Tools→Backtest window + Quick backtest |
| TradingTerminal.BacktestStudio | 1,338 | 12 | B I P | |
| TradingTerminal.Strategies.OrderFlowPressureMap | 1,327 | 10 | B I P | multi-ticker monitor |
| TradingTerminal.Settings | 1,290 | 11 | B I P | |
| TradingTerminal.Tests | 1,044 | 10 | dev | [WpfFact] tests |
| TradingTerminal.AdvancedMarketRegime | 818 | 5 | B I P | |
| TradingTerminal.Charts | 805 | 4 | B I P | WebView2 TradingView-style |
| TradingTerminal.Strategies.FilteredOrderFlow | 630 | 7 | B I P | arXiv:2507.22712 |
| TradingTerminal.Recording | 423 | 5 | B I P | |
| TradingTerminal.Ai | 347 | 4 | B I P | IAiAnalystClient seam only |
| DaxAlgo.Sdk | 74 | 3 | B I P | MIT plugin SDK |
| DaxAlgo.SamplePlugin | 61 | 1 | dev | |
| DaxAlgo.Sdk.Wpf | 0 | 0 | B I P | facade csproj, no source |

Linux tree (`src/linux/`) is **not indexed yet** — see MAINTENANCE.md before extending.
