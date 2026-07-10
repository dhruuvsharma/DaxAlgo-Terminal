# index/Charts — per-file index (Windows tree)

Generated 2026-07-10. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Charts/TradingTerminal.Charts/ChartsServiceCollectionExtensions.cs` | 15 | win | TradingTerminal.Charts | B I P | Y | DI registration for the TradingView-style Charts tool. Transient so each open gets |
| `src/windows/Charts/TradingTerminal.Charts/ChartsViewModel.cs` | 508 | win | TradingTerminal.Charts | B I P | Y | Set when a live candle arrived while paused, so resume can catch |
| `src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml.cs` | 123 | win | TradingTerminal.Charts | B I P | Y | PNG snapshot. WebView2 composits out-of-process, so |
| `src/windows/Charts/TradingTerminal.Charts/ChartsWindow.xaml` | 159 | win | TradingTerminal.Charts | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.Heatmap/AvaloniaUi/BookmapHeatmapAvaloniaWindow.axaml.cs` | 13 | win | TradingTerminal.Heatmap | B I P | Y | Avalonia (cross-platform) view for the Bookmap + VolBook tool — net9.0-leg counterpart |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs` | 525 | win | TradingTerminal.Heatmap | B I P | Y | How many time columns are visible at once (the scrolling window width). |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml.cs` | 52 | win | TradingTerminal.Heatmap | B I P | Y | Toolbar 📷: PNG snapshot of the whole window content (surface + read-outs). |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml` | 229 | win | TradingTerminal.Heatmap | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.Heatmap/BookmapSurface.cs` | 725 | win | TradingTerminal.Heatmap | B I P | Y | Called when the VM's buffers change — rebuild the cached data layer. |
| `src/windows/Charts/TradingTerminal.Heatmap/HeatmapServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.Heatmap | B I P | Y | DI registration for the Heatmap surface — the single combined |
| `src/windows/Charts/TradingTerminal.Heatmap/SingleInstrumentHeatmapViewModelBase.cs` | 252 | win | TradingTerminal.Heatmap | B I P | Y | Key under which this window's last selected instrument is remembered |
| `src/windows/Charts/TradingTerminal.OrderBook/AvaloniaUi/OrderBookAvaloniaWindow.axaml.cs` | 11 | win | TradingTerminal.OrderBook | B I P | Y | Avalonia (cross-platform) view for the Order Book — net9.0-leg counterpart to the |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookModels.cs` | 41 | win | TradingTerminal.OrderBook | B I P | Y | One display row of the ladder. |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.OrderBook | B I P | Y | DI registration for the standalone Order Book tool. Transient so each open |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs` | 1119 | win | TradingTerminal.OrderBook | B I P | Y | Cap on how many instruments the picker shows at once (the broker |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs` | 415 | win | TradingTerminal.OrderBook | B I P | Y | Columns reserved right of "now" for the ML forecast path (96 px |
| `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml` | 485 | win | TradingTerminal.OrderBook | B I P | N | XAML |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/AvaloniaUi/VolumeFootprintAvaloniaWindow.axaml.cs` | 12 | win | TradingTerminal.VolumeFootprint | B I P | Y | Avalonia (cross-platform) view for the Volume Footprint — net9.0-leg counterpart to the |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs` | 137 | win | TradingTerminal.VolumeFootprint | B I P | Y | Which POC series an overlay fit curve belongs to (drives the brush |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs` | 17 | win | TradingTerminal.VolumeFootprint | B I P | Y | DI registration for the Volume Footprint tool. Transient so each open gets |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs` | 1093 | win | TradingTerminal.VolumeFootprint | B I P | Y | Which brokers actually wire a native trade tape today (see the cube |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs` | 814 | win | TradingTerminal.VolumeFootprint | B I P | Y | Draws the connector lines for the total / buy / sell points-of-control |
| `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml` | 445 | win | TradingTerminal.VolumeFootprint | B I P | N | XAML |
