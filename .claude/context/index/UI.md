# index/UI — per-file index (Windows tree)

Generated 2026-07-17. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/UI/TradingTerminal.Settings/Archive/ArchiveActivityViewModel.cs` | 162 | win | TradingTerminal.Settings | B I P | Y | Period-by-period coverage — each window labelled Offloaded or Pending. |
| `src/windows/UI/TradingTerminal.Settings/Archive/ArchiveSettingsViewModel.cs` | 243 | win | TradingTerminal.Settings | B I P | Y | Settings tab for the market-data archive. Three sections: Telegram credentials + login, |
| `src/windows/UI/TradingTerminal.Settings/Archive/ArchiveUserFile.cs` | 69 | win | TradingTerminal.Settings | B I P | Y | Per-user JSON persistence for the archive settings tab. Layered into host configuration |
| `src/windows/UI/TradingTerminal.Settings/Archive/TelegramArchiveCredentialProtection.cs` | 50 | win | TradingTerminal.Settings | B I P | Y | Protection helpers for Telegram archive credentials. On Windows the secret is encrypted |
| `src/windows/UI/TradingTerminal.Settings/Authoring/AiCodegenUserFile.cs` | 92 | win | TradingTerminal.Settings | B I P | Y | Absolute path to |
| `src/windows/UI/TradingTerminal.Settings/Authoring/AiProvidersSettingsViewModel.cs` | 100 | win | TradingTerminal.Settings | B I P | Y | Store (or clear, when blank) the pasted key for a provider, DPAPI-encrypted. |
| `src/windows/UI/TradingTerminal.Settings/Authoring/AuthoringSessionStore.cs` | 152 | win | TradingTerminal.Settings | B I P | Y | One bubble as the user saw it. Kept separately from the model |
| `src/windows/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs` | 1214 | win | TradingTerminal.Settings | B I P | Y | Keeps the activity strip and the chat from growing without bound over |
| `src/windows/UI/TradingTerminal.Settings/Notifications/NotificationsSettingsViewModel.cs` | 226 | win | TradingTerminal.Settings | B I P | Y | Per-provider default text/vision model ids, pre-filled when the user picks a provider |
| `src/windows/UI/TradingTerminal.Settings/Notifications/NotificationsUserFile.cs` | 84 | win | TradingTerminal.Settings | B I P | Y | Writes the notifications section, preserving any other keys that may exist. |
| `src/windows/UI/TradingTerminal.Settings/Research/ResearchSettingsViewModel.cs` | 106 | win | TradingTerminal.Settings | B I P | Y | When on, the app launches the Python sidecar itself on startup (no |
| `src/windows/UI/TradingTerminal.Settings/Research/ResearchUserFile.cs` | 61 | win | TradingTerminal.Settings | B I P | Y | Absolute path to |
| `src/windows/UI/TradingTerminal.Settings/Support/SupportInfo.cs` | 41 | win | TradingTerminal.Settings | B I P | Y | The developer's inbox. Feedback is delivered via a |
| `src/windows/UI/TradingTerminal.Settings/Support/SupportViewModel.cs` | 99 | win | TradingTerminal.Settings | B I P | Y | The note the user types to the developer. |
| `src/windows/UI/TradingTerminal.StrategyComposer/AuthoredStrategyViewComposer.cs` | 32 | win | TradingTerminal.StrategyComposer | B I P | Y | Must run on the UI thread — it builds WPF controls. Both |
| `src/windows/UI/TradingTerminal.StrategyComposer/ComposedStrategyView.xaml.cs` | 297 | win | TradingTerminal.StrategyComposer | B I P | Y | The panels this composition holds, in display order — for tests and |
| `src/windows/UI/TradingTerminal.StrategyComposer/ComposedStrategyView.xaml` | 177 | win | TradingTerminal.StrategyComposer | B I P | N | XAML |
| `src/windows/UI/TradingTerminal.UI.Core/BarIndicators.cs` | 157 | win | TradingTerminal.UI.Core | B I P | Y | Returns (mean, stdev, upper, lower) arrays aligned with bars. |
| `src/windows/UI/TradingTerminal.UI.Core/BusyState.cs` | 74 | win | TradingTerminal.UI.Core | B I P | Y | True while at least one |
| `src/windows/UI/TradingTerminal.UI.Core/Catalog/StrategyCatalogViewModel.cs` | 53 | win | TradingTerminal.UI.Core | B I P | Y | Human-readable detail block for the currently selected strategy. |
| `src/windows/UI/TradingTerminal.UI.Core/Diagnostics/PluginFaultTracker.cs` | 31 | win | TradingTerminal.UI.Core | B I P | Y | Records one fault for |
| `src/windows/UI/TradingTerminal.UI.Core/ISignalGeneratorRouterFactory.cs` | 18 | win | TradingTerminal.UI.Core | B I P | Y | Default impl — vanilla |
| `src/windows/UI/TradingTerminal.UI.Core/InstrumentPickerFilter.cs` | 114 | win | TradingTerminal.UI.Core | B I P | Y | Rows to show for a |
| `src/windows/UI/TradingTerminal.UI.Core/LastInstrumentStore.cs` | 75 | win | TradingTerminal.UI.Core | B I P | Y | The canonical symbol last selected under |
| `src/windows/UI/TradingTerminal.UI.Core/LiveSignalStrategyViewModelBase.cs` | 940 | win | TradingTerminal.UI.Core | B I P | Y | Cap on how many instruments the picker shows at once. The broker |
| `src/windows/UI/TradingTerminal.UI.Core/LiveStrategyHostServices.cs` | 41 | win | TradingTerminal.UI.Core | B I P | Y | Bundle of canonical-pipeline dependencies that every live strategy host needs. Passed as |
| `src/windows/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs` | 91 | win | TradingTerminal.UI.Core | B I P | Y | Convenience append used by strategy/tab view-models — stamps the entry with the |
| `src/windows/UI/TradingTerminal.UI.Core/Presets/StrategyViewPreset.cs` | 18 | win | TradingTerminal.UI.Core | B I P | Y | A named snapshot of a strategy window's view options, persisted per user |
| `src/windows/UI/TradingTerminal.UI.Core/Presets/ToolPresetStore.cs` | 98 | win | TradingTerminal.UI.Core | B I P | Y | Test seam: redirect the store directory. |
| `src/windows/UI/TradingTerminal.UI.Core/SignalEntry.cs` | 22 | win | TradingTerminal.UI.Core | B I P | Y | One signal row in the live signal log. Produced every time the |
| `src/windows/UI/TradingTerminal.UI.Core/SignalGeneratorRouter.cs` | 98 | win | TradingTerminal.UI.Core | B I P | Y | Most recent live tick; used to price synthetic fills. |
| `src/windows/UI/TradingTerminal.UI.Core/Strategies/ParameterEditorItem.cs` | 92 | win | TradingTerminal.UI.Core | B I P | Y | Numeric value for both |
| `src/windows/UI/TradingTerminal.UI.Core/Strategies/StrategyFactory.cs` | 95 | win | TradingTerminal.UI.Core | B I P | Y | DI-backed catalog. Each strategy registered in DI must also register a |
| `src/windows/UI/TradingTerminal.UI.Core/Strategies/StrategyParametersViewModel.cs` | 48 | win | TradingTerminal.UI.Core | B I P | Y | Builds an editor panel from a schema, seeded with defaults. |
| `src/windows/UI/TradingTerminal.UI.Core/TaskExtensions.cs` | 29 | win | TradingTerminal.UI.Core | B I P | Y | Fires the task and logs any exception via |
| `src/windows/UI/TradingTerminal.UI.Core/TradeableInstrument.cs` | 145 | win | TradingTerminal.UI.Core | B I P | Y | App sets this once at startup to a registry-backed provider. When null |
| `src/windows/UI/TradingTerminal.UI.Core/UiFile.cs` | 22 | win | TradingTerminal.UI.Core | B I P | Y | Show an open-file picker. |
| `src/windows/UI/TradingTerminal.UI.Core/UiThread.cs` | 50 | win | TradingTerminal.UI.Core | B I P | Y | Runs |
| `src/windows/UI/TradingTerminal.UI.Core/ViewModelBase.cs` | 8 | win | TradingTerminal.UI.Core | B I P | Y | Base class for all view-models. Inherits CommunityToolkit's |
