# Linux index / Charts

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/Charts/TradingTerminal.Heatmap/AvaloniaUi/BookmapHeatmapAvaloniaWindow.axaml.cs` | 13 | linux | TradingTerminal.Heatmap | product | Y | Avalonia (cross-platform) view for the Bookmap + VolBook tool — net9.0-leg counterpart |
| `src/linux/Charts/TradingTerminal.Heatmap/AvaloniaUi/BookmapHeatmapAvaloniaWindow.axaml` | 46 | linux | TradingTerminal.Heatmap | product | N | UI |
| `src/linux/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs` | 395 | linux | TradingTerminal.Heatmap | product | Y | How many time columns are visible at once (the scrolling window width). |
| `src/linux/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml.cs` | 41 | linux | TradingTerminal.Heatmap | product | Y | Hosts the combined Bookmap + VolBook view. Pure presentation: it binds the |
| `src/linux/Charts/TradingTerminal.Heatmap/BookmapHeatmapWindow.xaml` | 161 | linux | TradingTerminal.Heatmap | product | N | UI |
| `src/linux/Charts/TradingTerminal.Heatmap/BookmapSurface.cs` | 724 | linux | TradingTerminal.Heatmap | product | Y | Called when the VM's buffers change — rebuild the cached data layer. |
| `src/linux/Charts/TradingTerminal.Heatmap/HeatmapServiceCollectionExtensions.cs` | 17 | linux | TradingTerminal.Heatmap | product | Y | DI registration for the Heatmap surface — the single combined |
| `src/linux/Charts/TradingTerminal.Heatmap/SingleInstrumentHeatmapViewModelBase.cs` | 250 | linux | TradingTerminal.Heatmap | product | Y | Redraw cadence — decoupled from the data feed so a fast book/tape |
| `src/linux/Charts/TradingTerminal.OrderBook/AvaloniaUi/OrderBookAvaloniaWindow.axaml.cs` | 11 | linux | TradingTerminal.OrderBook | product | Y | Avalonia (cross-platform) view for the Order Book — net9.0-leg counterpart to the |
| `src/linux/Charts/TradingTerminal.OrderBook/AvaloniaUi/OrderBookAvaloniaWindow.axaml` | 69 | linux | TradingTerminal.OrderBook | product | N | UI |
| `src/linux/Charts/TradingTerminal.OrderBook/OrderBookModels.cs` | 41 | linux | TradingTerminal.OrderBook | product | Y | One display row of the ladder. |
| `src/linux/Charts/TradingTerminal.OrderBook/OrderBookServiceCollectionExtensions.cs` | 17 | linux | TradingTerminal.OrderBook | product | Y | DI registration for the standalone Order Book tool. Transient so each open |
| `src/linux/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs` | 636 | linux | TradingTerminal.OrderBook | product | Y | Cap on how many instruments the picker shows at once (the broker |
| `src/linux/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs` | 313 | linux | TradingTerminal.OrderBook | product | Y | Hosts the standalone Order Book window. Pure view: the owns the |
| `src/linux/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml` | 300 | linux | TradingTerminal.OrderBook | product | N | UI |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/AvaloniaUi/VolumeFootprintAvaloniaWindow.axaml.cs` | 12 | linux | TradingTerminal.VolumeFootprint | product | Y | Avalonia (cross-platform) view for the Volume Footprint — net9.0-leg counterpart to the |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/AvaloniaUi/VolumeFootprintAvaloniaWindow.axaml` | 45 | linux | TradingTerminal.VolumeFootprint | product | N | UI |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintModels.cs` | 131 | linux | TradingTerminal.VolumeFootprint | product | Y | Which POC series an overlay fit curve belongs to (drives the brush |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintServiceCollectionExtensions.cs` | 17 | linux | TradingTerminal.VolumeFootprint | product | Y | DI registration for the Volume Footprint tool. Transient so each open gets |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs` | 739 | linux | TradingTerminal.VolumeFootprint | product | Y | Which brokers actually wire a native trade tape today (see the cube |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs` | 677 | linux | TradingTerminal.VolumeFootprint | product | Y | Draws the connector lines for the total / buy / sell points-of-control |
| `src/linux/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml` | 326 | linux | TradingTerminal.VolumeFootprint | product | N | UI |
