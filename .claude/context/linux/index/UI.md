# Linux index / UI

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `src/linux/UI/TradingTerminal.Settings/Archive/ArchiveActivityViewModel.cs` | 162 | linux | TradingTerminal.Settings | product | Y | Period-by-period coverage — each window labelled Offloaded or Pending. |
| `src/linux/UI/TradingTerminal.Settings/Archive/ArchiveSettingsViewModel.cs` | 243 | linux | TradingTerminal.Settings | product | Y | Settings tab for the market-data archive. Three sections: Telegram credentials + login, |
| `src/linux/UI/TradingTerminal.Settings/Archive/ArchiveUserFile.cs` | 69 | linux | TradingTerminal.Settings | product | Y | Per-user JSON persistence for the archive settings tab. Layered into host configuration |
| `src/linux/UI/TradingTerminal.Settings/Archive/TelegramArchiveCredentialProtection.cs` | 50 | linux | TradingTerminal.Settings | product | Y | Protection helpers for Telegram archive credentials. On Windows the secret is encrypted |
| `src/linux/UI/TradingTerminal.Settings/Authoring/StrategyAuthoringViewModel.cs` | 149 | linux | TradingTerminal.Settings | product | Y | Auto-generated editor for the compiled strategy's tunables, or null when it |
| `src/linux/UI/TradingTerminal.Settings/Notifications/NotificationsSettingsViewModel.cs` | 226 | linux | TradingTerminal.Settings | product | Y | Per-provider default text/vision model ids, pre-filled when the user picks a provider |
| `src/linux/UI/TradingTerminal.Settings/Notifications/NotificationsUserFile.cs` | 84 | linux | TradingTerminal.Settings | product | Y | Writes the notifications section, preserving any other keys that may exist. |
| `src/linux/UI/TradingTerminal.Settings/Research/ResearchSettingsViewModel.cs` | 106 | linux | TradingTerminal.Settings | product | Y | When on, the app launches the Python sidecar itself on startup (no |
| `src/linux/UI/TradingTerminal.Settings/Research/ResearchUserFile.cs` | 61 | linux | TradingTerminal.Settings | product | Y | Absolute path to |
| `src/linux/UI/TradingTerminal.Settings/Support/SupportInfo.cs` | 41 | linux | TradingTerminal.Settings | product | Y | The developer's inbox. Feedback is delivered via a |
| `src/linux/UI/TradingTerminal.Settings/Support/SupportViewModel.cs` | 99 | linux | TradingTerminal.Settings | product | Y | The note the user types to the developer. |
| `src/linux/UI/TradingTerminal.UI.Avalonia/GenericStrategyWindow.axaml.cs` | 15 | linux | TradingTerminal.UI.Avalonia | product | Y | Generic Avalonia window for any LiveSignalStrategyViewModelBase: binds the common surface |
| `src/linux/UI/TradingTerminal.UI.Avalonia/GenericStrategyWindow.axaml` | 59 | linux | TradingTerminal.UI.Avalonia | product | N | UI |
| `src/linux/UI/TradingTerminal.UI.Core/BarIndicators.cs` | 157 | linux | TradingTerminal.UI.Core | product | Y | Returns (mean, stdev, upper, lower) arrays aligned with bars. |
| `src/linux/UI/TradingTerminal.UI.Core/BusyState.cs` | 74 | linux | TradingTerminal.UI.Core | product | Y | True while at least one |
| `src/linux/UI/TradingTerminal.UI.Core/Catalog/StrategyCatalogViewModel.cs` | 53 | linux | TradingTerminal.UI.Core | product | Y | Human-readable detail block for the currently selected strategy. |
| `src/linux/UI/TradingTerminal.UI.Core/ISignalGeneratorRouterFactory.cs` | 18 | linux | TradingTerminal.UI.Core | product | Y | Default impl — vanilla |
| `src/linux/UI/TradingTerminal.UI.Core/LiveSignalStrategyViewModelBase.cs` | 776 | linux | TradingTerminal.UI.Core | product | Y | Cap on how many instruments the picker shows at once. The broker |
| `src/linux/UI/TradingTerminal.UI.Core/LiveStrategyHostServices.cs` | 41 | linux | TradingTerminal.UI.Core | product | Y | Bundle of canonical-pipeline dependencies that every live strategy host needs. Passed as |
| `src/linux/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs` | 59 | linux | TradingTerminal.UI.Core | product | Y | Convenience append used by strategy/tab view-models — stamps the entry with the |
| `src/linux/UI/TradingTerminal.UI.Core/SignalEntry.cs` | 22 | linux | TradingTerminal.UI.Core | product | Y | One signal row in the live signal log. Produced every time the |
| `src/linux/UI/TradingTerminal.UI.Core/SignalGeneratorRouter.cs` | 98 | linux | TradingTerminal.UI.Core | product | Y | Most recent live tick; used to price synthetic fills. |
| `src/linux/UI/TradingTerminal.UI.Core/Strategies/ParameterEditorItem.cs` | 92 | linux | TradingTerminal.UI.Core | product | Y | Numeric value for both |
| `src/linux/UI/TradingTerminal.UI.Core/Strategies/StrategyFactory.cs` | 53 | linux | TradingTerminal.UI.Core | product | Y | DI-backed factory. Each registered strategy must also register a |
| `src/linux/UI/TradingTerminal.UI.Core/Strategies/StrategyParametersViewModel.cs` | 48 | linux | TradingTerminal.UI.Core | product | Y | Builds an editor panel from a schema, seeded with defaults. |
| `src/linux/UI/TradingTerminal.UI.Core/TaskExtensions.cs` | 29 | linux | TradingTerminal.UI.Core | product | Y | Fires the task and logs any exception via |
| `src/linux/UI/TradingTerminal.UI.Core/TradeableInstrument.cs` | 145 | linux | TradingTerminal.UI.Core | product | Y | App sets this once at startup to a registry-backed provider. When null |
| `src/linux/UI/TradingTerminal.UI.Core/UiFile.cs` | 22 | linux | TradingTerminal.UI.Core | product | Y | Show an open-file picker. |
| `src/linux/UI/TradingTerminal.UI.Core/UiThread.cs` | 50 | linux | TradingTerminal.UI.Core | product | Y | Runs |
| `src/linux/UI/TradingTerminal.UI.Core/ViewModelBase.cs` | 8 | linux | TradingTerminal.UI.Core | product | Y | Base class for all view-models. Inherits CommunityToolkit's |
