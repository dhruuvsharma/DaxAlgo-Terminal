# index/Charts — per-file index (Windows tree)

Generated from the current source tree. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Charts/TradingTerminal.Charts/ChartsPanel.xaml.cs` | 173 | win | TradingTerminal.Charts | B I P | Y | Which parts of the panel are switched on. Set it before the |
| `src/windows/Charts/TradingTerminal.Charts/ChartsPanel.xaml` | 157 | win | TradingTerminal.Charts | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.Charts/ChartsPanelFeatures.cs` | 53 | win | TradingTerminal.Charts | B I P | Y | Symbol/timeframe selectors, presets, pause/export, the ? help and the ⚙ rail toggle. |
| `src/windows/Charts/TradingTerminal.Charts/ChartsServiceCollectionExtensions.cs` | 15 | win | TradingTerminal.Charts | B I P | Y | DI registration for the TradingView-style Charts tool. Transient so each open gets |
| `src/windows/Charts/TradingTerminal.Charts/ChartsViewModel.cs` | 560 | win | TradingTerminal.Charts | B I P | Y | Non-null when this view-model lives inside a strategy window rather than the |
| `src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml.cs` | 14 | win | TradingTerminal.Charts | B I P | Y | The standalone Charts window: a frame around with every feature on. The |
| `src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml` | 17 | win | TradingTerminal.Charts | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs` | 525 | win | TradingTerminal.Heatmap | B I P | Y | How many time columns are visible at once (the scrolling window width). |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml.cs` | 54 | win | TradingTerminal.Heatmap | B I P | Y | Toolbar 📷: PNG snapshot of the whole window content (surface + read-outs). |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml` | 229 | win | TradingTerminal.Heatmap | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapSurface.cs` | 725 | win | TradingTerminal.Heatmap | B I P | Y | Called when the VM's buffers change — rebuild the cached data layer. |
| `src/windows/Charts/TradingTerminal.Heatmap/HeatmapServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.Heatmap | B I P | Y | DI registration for the Heatmap surface — the single combined |
| `src/windows/Charts/TradingTerminal.Heatmap/SingleInstrumentHeatmapViewModelBase.cs` | 252 | win | TradingTerminal.Heatmap | B I P | Y | Key under which this window's last selected instrument is remembered |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookModels.cs` | 41 | win | TradingTerminal.OrderBook | B I P | Y | One display row of the ladder. |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookPanel.xaml.cs` | 448 | win | TradingTerminal.OrderBook | B I P | Y | Which parts of the panel are switched on. Set it before the |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookPanel.xaml` | 492 | win | TradingTerminal.OrderBook | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookPanelFeatures.cs` | 64 | win | TradingTerminal.OrderBook | B I P | Y | Instrument picker, presets, pause/export/snapshot. Off when something else owns the |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.OrderBook | B I P | Y | DI registration for the standalone Order Book tool. Transient so each open |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs` | 1154 | win | TradingTerminal.OrderBook | B I P | Y | Cap on how many instruments the picker shows at once (the broker |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs` | 14 | win | TradingTerminal.OrderBook | B I P | Y | The standalone Order Book window: a frame around with every feature on. |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml` | 18 | win | TradingTerminal.OrderBook | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs` | 137 | win | TradingTerminal.VolumeFootprint | B I P | Y | Which POC series an overlay fit curve belongs to (drives the brush |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintPanel.xaml.cs` | 862 | win | TradingTerminal.VolumeFootprint | B I P | Y | Which parts of the panel are switched on. Set it before the |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintPanel.xaml` | 444 | win | TradingTerminal.VolumeFootprint | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintPanelFeatures.cs` | 74 | win | TradingTerminal.VolumeFootprint | B I P | Y | Instrument/timeframe selectors, the Regression · Overlays · Display menus, zoom, the ? |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.VolumeFootprint | B I P | Y | DI registration for the Volume Footprint tool. Transient so each open gets |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs` | 1131 | win | TradingTerminal.VolumeFootprint | B I P | Y | Which brokers actually wire a native trade tape today (see the cube |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs` | 14 | win | TradingTerminal.VolumeFootprint | B I P | Y | The standalone Volume Footprint window: a frame around with every |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml` | 18 | win | TradingTerminal.VolumeFootprint | B I P | N | XAML |
