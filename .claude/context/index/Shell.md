# index/Shell — per-file index (Windows tree)

Generated 2026-07-17. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Shell/TradingTerminal.App.Basic/App.xaml.cs` | 306 | win | TradingTerminal.App.Basic | B | Y | Friendly once-per-launch "support the developer" nudge, after a short randomised delay. |
| `src/windows/Shell/TradingTerminal.App.Basic/App.xaml` | 36 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveActivityView.xaml.cs` | 8 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveActivityView.xaml` | 149 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveSettingsView.xaml.cs` | 54 | win | TradingTerminal.App.Basic | B | Y | Code-behind is PasswordBox plumbing only (Password is not a bindable DP): the |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveSettingsView.xaml` | 192 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramArchiveLogin.cs` | 89 | win | TradingTerminal.App.Basic | B | Y | App-layer implementation of the seam used by the login |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramArchiveOptionsPostConfigure.cs` | 29 | win | TradingTerminal.App.Basic | B | Y | Runs after is bound from configuration and replaces the |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramPromptDialog.xaml.cs` | 43 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramPromptDialog.xaml` | 52 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Archive/WpfTelegramAuthPrompt.cs` | 53 | win | TradingTerminal.App.Basic | B | Y | WPF-side bridge for the Telegram MTProto login flow. WTelegramClient's Config callback |
| `src/windows/Shell/TradingTerminal.App.Basic/Authoring/AiProvidersSettingsView.xaml.cs` | 11 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Authoring/AiProvidersSettingsView.xaml` | 58 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Authoring/StrategyAuthoringView.xaml.cs` | 48 | win | TradingTerminal.App.Basic | B | Y | AI Strategy Builder pane: a chat with the model, the files it |
| `src/windows/Shell/TradingTerminal.App.Basic/Authoring/StrategyAuthoringView.xaml` | 464 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/BrokerMetering/BrokerApiChipViewModel.cs` | 95 | win | TradingTerminal.App.Basic | B | Y | Short three-letter-ish label shown on the chip ("IB", "NT", "CT", "AL"). |
| `src/windows/Shell/TradingTerminal.App.Basic/BrokerMetering/BrokerApiMeterViewModel.cs` | 90 | win | TradingTerminal.App.Basic | B | Y | One chip per broker that has had at least one call recorded. |
| `src/windows/Shell/TradingTerminal.App.Basic/Composition/AppDependencyInjection.cs` | 283 | win | TradingTerminal.App.Basic | B | Y | Strategy plug-ins: RSI, Cumulative Delta, plus the signal-mode wrappers |
| `src/windows/Shell/TradingTerminal.App.Basic/Logging/ObservableCollectionLogSink.cs` | 23 | win | TradingTerminal.App.Basic | B | Y | Serilog sink that forwards rendered messages into an |
| `src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml.cs` | 99 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml` | 1020 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/MainWindowViewModel.cs` | 754 | win | TradingTerminal.App.Basic | B | Y | Surfaces a persistent amber "SIMULATED DATA" banner while the Simulated broker is |
| `src/windows/Shell/TradingTerminal.App.Basic/Notifications/NotificationsSettingsView.xaml.cs` | 11 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Notifications/NotificationsSettingsView.xaml` | 206 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginConsentDialog.xaml.cs` | 81 | win | TradingTerminal.App.Basic | B | Y | Shows the dialog modally and returns the user's decision. Static so the |
| `src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginConsentDialog.xaml` | 80 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginManagerView.xaml.cs` | 11 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginManagerView.xaml` | 224 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginManagerViewModel.cs` | 369 | win | TradingTerminal.App.Basic | B | Y | One row in the plugins list — a loaded plugin OR one |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/IShellFactory.cs` | 25 | win | TradingTerminal.App.Basic | B | Y | Builds the login window with its view-model wired in. |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/IShellWindowHost.cs` | 60 | win | TradingTerminal.App.Basic | B | Y | The presenter that paints the shell "Opening…" curtain. Wired by the shell |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/LoginShellFactory.cs` | 21 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/MainShellFactory.cs` | 19 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/ShellWindowHost.cs` | 103 | win | TradingTerminal.App.Basic | B | Y | Default : owns the single-instance window registry and the generic |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/ThemeMenuOption.cs` | 20 | win | TradingTerminal.App.Basic | B | Y | One entry in the View → Theme menu — the theme id |
| `src/windows/Shell/TradingTerminal.App.Basic/Shell/ToolHostWindow.cs` | 44 | win | TradingTerminal.App.Basic | B | Y | Builds a themed host window around an already-DataContext'd tool view. |
| `src/windows/Shell/TradingTerminal.App.Basic/Support/ISupportPrompt.cs` | 19 | win | TradingTerminal.App.Basic | B | Y | Called once after the main window appears. Honours the once-per-launch + random |
| `src/windows/Shell/TradingTerminal.App.Basic/Support/SupportPrompt.cs` | 74 | win | TradingTerminal.App.Basic | B | Y | Default . Shows the thank-you / feedback window at most once per |
| `src/windows/Shell/TradingTerminal.App.Basic/Support/SupportWindow.xaml.cs` | 29 | win | TradingTerminal.App.Basic | B | Y | The "Support the developer" dialog. Code-behind does nothing but bridge the view-model's |
| `src/windows/Shell/TradingTerminal.App.Basic/Support/SupportWindow.xaml` | 122 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeStudioView.xaml.cs` | 11 | win | TradingTerminal.App.Basic | B | Y |  |
| `src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeStudioView.xaml` | 208 | win | TradingTerminal.App.Basic | B | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeStudioViewModel.cs` | 204 | win | TradingTerminal.App.Basic | B | Y | Name used when saving/exporting the current edits as a custom theme. |
| `src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeTokenViewModel.cs` | 191 | win | TradingTerminal.App.Basic | B | Y | Preview swatch fill, kept current with the colour. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/App.xaml.cs` | 322 | win | TradingTerminal.App.Intermediate | I | Y | Friendly once-per-launch "support the developer" nudge, after a short randomised delay. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/App.xaml` | 36 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveActivityView.xaml.cs` | 8 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveActivityView.xaml` | 149 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveSettingsView.xaml.cs` | 54 | win | TradingTerminal.App.Intermediate | I | Y | Code-behind is PasswordBox plumbing only (Password is not a bindable DP): the |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveSettingsView.xaml` | 192 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramArchiveLogin.cs` | 89 | win | TradingTerminal.App.Intermediate | I | Y | App-layer implementation of the seam used by the login |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramArchiveOptionsPostConfigure.cs` | 29 | win | TradingTerminal.App.Intermediate | I | Y | Runs after is bound from configuration and replaces the |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramPromptDialog.xaml.cs` | 43 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramPromptDialog.xaml` | 52 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Archive/WpfTelegramAuthPrompt.cs` | 53 | win | TradingTerminal.App.Intermediate | I | Y | WPF-side bridge for the Telegram MTProto login flow. WTelegramClient's Config callback |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/AiProvidersSettingsView.xaml.cs` | 11 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/AiProvidersSettingsView.xaml` | 58 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/StrategyAuthoringView.xaml.cs` | 48 | win | TradingTerminal.App.Intermediate | I | Y | AI Strategy Builder pane: a chat with the model, the files it |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/StrategyAuthoringView.xaml` | 464 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/BrokerMetering/BrokerApiChipViewModel.cs` | 95 | win | TradingTerminal.App.Intermediate | I | Y | Short three-letter-ish label shown on the chip ("IB", "NT", "CT", "AL"). |
| `src/windows/Shell/TradingTerminal.App.Intermediate/BrokerMetering/BrokerApiMeterViewModel.cs` | 90 | win | TradingTerminal.App.Intermediate | I | Y | One chip per broker that has had at least one call recorded. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Composition/AppDependencyInjection.cs` | 283 | win | TradingTerminal.App.Intermediate | I | Y | Strategy plug-ins: RSI, Cumulative Delta, plus the signal-mode wrappers |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Logging/ObservableCollectionLogSink.cs` | 23 | win | TradingTerminal.App.Intermediate | I | Y | Serilog sink that forwards rendered messages into an |
| `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindow.xaml.cs` | 99 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindow.xaml` | 1041 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindowViewModel.cs` | 754 | win | TradingTerminal.App.Intermediate | I | Y | Surfaces a persistent amber "SIMULATED DATA" banner while the Simulated broker is |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Notifications/NotificationsSettingsView.xaml.cs` | 11 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Notifications/NotificationsSettingsView.xaml` | 206 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginConsentDialog.xaml.cs` | 81 | win | TradingTerminal.App.Intermediate | I | Y | Shows the dialog modally and returns the user's decision. Static so the |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginConsentDialog.xaml` | 80 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginManagerView.xaml.cs` | 11 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginManagerView.xaml` | 224 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginManagerViewModel.cs` | 369 | win | TradingTerminal.App.Intermediate | I | Y | One row in the plugins list — a loaded plugin OR one |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/IShellFactory.cs` | 25 | win | TradingTerminal.App.Intermediate | I | Y | Builds the login window with its view-model wired in. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/IShellWindowHost.cs` | 60 | win | TradingTerminal.App.Intermediate | I | Y | The presenter that paints the shell "Opening…" curtain. Wired by the shell |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/LoginShellFactory.cs` | 21 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/MainShellFactory.cs` | 19 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ShellWindowHost.cs` | 103 | win | TradingTerminal.App.Intermediate | I | Y | Default : owns the single-instance window registry and the generic |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ThemeMenuOption.cs` | 20 | win | TradingTerminal.App.Intermediate | I | Y | One entry in the View → Theme menu — the theme id |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ToolHostWindow.cs` | 44 | win | TradingTerminal.App.Intermediate | I | Y | Builds a themed host window around an already-DataContext'd tool view. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Support/ISupportPrompt.cs` | 19 | win | TradingTerminal.App.Intermediate | I | Y | Called once after the main window appears. Honours the once-per-launch + random |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Support/SupportPrompt.cs` | 74 | win | TradingTerminal.App.Intermediate | I | Y | Default . Shows the thank-you / feedback window at most once per |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Support/SupportWindow.xaml.cs` | 29 | win | TradingTerminal.App.Intermediate | I | Y | The "Support the developer" dialog. Code-behind does nothing but bridge the view-model's |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Support/SupportWindow.xaml` | 122 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeStudioView.xaml.cs` | 11 | win | TradingTerminal.App.Intermediate | I | Y |  |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeStudioView.xaml` | 208 | win | TradingTerminal.App.Intermediate | I | N | XAML |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeStudioViewModel.cs` | 204 | win | TradingTerminal.App.Intermediate | I | Y | Name used when saving/exporting the current edits as a custom theme. |
| `src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeTokenViewModel.cs` | 191 | win | TradingTerminal.App.Intermediate | I | Y | Preview swatch fill, kept current with the colour. |
| `src/windows/Shell/TradingTerminal.Login/AiKeyStore.cs` | 119 | win | TradingTerminal.Login | B I P | Y | Provider ids that currently have a stored key. |
| `src/windows/Shell/TradingTerminal.Login/BrokerLoginFormBase.cs` | 272 | win | TradingTerminal.Login | B I P | Y | Two/three-letter square-badge text (e.g. "BN", "IB"). |
| `src/windows/Shell/TradingTerminal.Login/BrokerLoginFormFactory.cs` | 32 | win | TradingTerminal.Login | B I P | Y | Default . Each per-broker form is registered in DI as |
| `src/windows/Shell/TradingTerminal.Login/CredentialStore.cs` | 59 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/CredentialStoreAiKeyResolver.cs` | 23 | win | TradingTerminal.Login | B I P | Y | Resolves AI-provider keys for the codegen factory from the DPAPI , falling |
| `src/windows/Shell/TradingTerminal.Login/Forms/AlpacaLoginForm.xaml.cs` | 25 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/AlpacaLoginForm.xaml` | 53 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/AlpacaLoginFormViewModel.cs` | 100 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/BinanceLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/BinanceLoginForm.xaml` | 29 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/BinanceLoginFormViewModel.cs` | 44 | win | TradingTerminal.Login | B I P | Y | Login form for Binance public market data. There are no credentials to |
| `src/windows/Shell/TradingTerminal.Login/Forms/BybitLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/BybitLoginForm.xaml` | 20 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/BybitLoginFormViewModel.cs` | 24 | win | TradingTerminal.Login | B I P | Y | Login form for Bybit public market data — no credentials (keyless, like |
| `src/windows/Shell/TradingTerminal.Login/Forms/CTraderLoginForm.xaml.cs` | 34 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/CTraderLoginForm.xaml` | 107 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/CTraderLoginFormViewModel.cs` | 246 | win | TradingTerminal.Login | B I P | Y | True while |
| `src/windows/Shell/TradingTerminal.Login/Forms/CoinbaseLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/CoinbaseLoginForm.xaml` | 20 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/CoinbaseLoginFormViewModel.cs` | 24 | win | TradingTerminal.Login | B I P | Y | Login form for Coinbase public market data — no credentials (keyless, like |
| `src/windows/Shell/TradingTerminal.Login/Forms/IbLoginForm.xaml.cs` | 25 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/IbLoginForm.xaml` | 75 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/IbLoginFormViewModel.cs` | 129 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/IronBeamLoginForm.xaml.cs` | 25 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/IronBeamLoginForm.xaml` | 40 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/IronBeamLoginFormViewModel.cs` | 93 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/KrakenLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/KrakenLoginForm.xaml` | 20 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/KrakenLoginFormViewModel.cs` | 24 | win | TradingTerminal.Login | B I P | Y | Login form for Kraken public market data — no credentials (keyless, like |
| `src/windows/Shell/TradingTerminal.Login/Forms/LondonStrategicEdgeLoginForm.xaml.cs` | 25 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/LondonStrategicEdgeLoginForm.xaml` | 31 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/LondonStrategicEdgeLoginFormViewModel.cs` | 74 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/NinjaLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/NinjaLoginForm.xaml` | 54 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/NinjaLoginFormViewModel.cs` | 81 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/OkxLoginForm.xaml.cs` | 11 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/OkxLoginForm.xaml` | 20 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/OkxLoginFormViewModel.cs` | 24 | win | TradingTerminal.Login | B I P | Y | Login form for OKX public market data — no credentials (keyless, like |
| `src/windows/Shell/TradingTerminal.Login/Forms/UpstoxLoginForm.xaml.cs` | 25 | win | TradingTerminal.Login | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.Login/Forms/UpstoxLoginForm.xaml` | 73 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/Forms/UpstoxLoginFormViewModel.cs` | 200 | win | TradingTerminal.Login | B I P | Y | Status text shown beneath the auth buttons (success or a user-facing error). |
| `src/windows/Shell/TradingTerminal.Login/LoginServiceCollectionExtensions.cs` | 91 | win | TradingTerminal.Login | B I P | Y | The login window/flow plus the KEYLESS broker forms (public crypto feeds — |
| `src/windows/Shell/TradingTerminal.Login/LoginViewModel.cs` | 540 | win | TradingTerminal.Login | B I P | Y | The forms as their concrete base type, pre-sorted Keyless → Credentialed → |
| `src/windows/Shell/TradingTerminal.Login/LoginWindow.xaml.cs` | 37 | win | TradingTerminal.Login | B I P | Y | Shell-only code-behind. Every broker row is projected through the single DataTemplate in |
| `src/windows/Shell/TradingTerminal.Login/LoginWindow.xaml` | 646 | win | TradingTerminal.Login | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.Login/ServiceDependencyViewModel.cs` | 170 | win | TradingTerminal.Login | B I P | Y | The live state of an external dependency the terminal talks to but |
| `src/windows/Shell/TradingTerminal.Login/StoredCredentials.cs` | 177 | win | TradingTerminal.Login | B I P | Y | Which broker the user last signed in with. Drives the form shown |
| `src/windows/Shell/TradingTerminal.UI/Controls/BusyOverlay.xaml.cs` | 56 | win | TradingTerminal.UI | B I P | Y | When true the curtain is shown and blocks input; when false it |
| `src/windows/Shell/TradingTerminal.UI/Controls/BusyOverlay.xaml` | 57 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/InjectedFormHost.cs` | 36 | win | TradingTerminal.UI | B I P | Y | Builds the view for a given view-model. Assigned once at runtime by |
| `src/windows/Shell/TradingTerminal.UI/Controls/InstrumentPicker.xaml.cs` | 101 | win | TradingTerminal.UI | B I P | Y | Resource key under which the shared |
| `src/windows/Shell/TradingTerminal.UI/Controls/InstrumentPicker.xaml` | 65 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/ParamSlider.xaml.cs` | 73 | win | TradingTerminal.UI | B I P | Y | Compact labeled slider with a live value readout, used for continuous tunables |
| `src/windows/Shell/TradingTerminal.UI/Controls/ParamSlider.xaml` | 22 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/ParamSpinner.xaml.cs` | 87 | win | TradingTerminal.UI | B I P | Y | Caption shown above the spinner. Bake any unit suffix into the text |
| `src/windows/Shell/TradingTerminal.UI/Controls/ParamSpinner.xaml` | 19 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/SimulatedDataBanner.cs` | 84 | win | TradingTerminal.UI | B I P | Y | Docks a fresh banner above |
| `src/windows/Shell/TradingTerminal.UI/Controls/Spinner.xaml.cs` | 99 | win | TradingTerminal.UI | B I P | Y | Outer width/height of the spinner, in DIPs. |
| `src/windows/Shell/TradingTerminal.UI/Controls/Spinner.xaml` | 24 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/StrategyChromeBar.xaml.cs` | 109 | win | TradingTerminal.UI | B I P | Y | File-name stem for the PNG snapshot (e.g. |
| `src/windows/Shell/TradingTerminal.UI/Controls/StrategyChromeBar.xaml` | 66 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/StrategySetupHost.cs` | 109 | win | TradingTerminal.UI | B I P | Y | Strategy name shown large at the top of the branding pane (bind |
| `src/windows/Shell/TradingTerminal.UI/Controls/StrategySetupHost.xaml` | 85 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Controls/ViewExport.cs` | 56 | win | TradingTerminal.UI | B I P | Y | Renders |
| `src/windows/Shell/TradingTerminal.UI/Converters/BarFractionToGridLengthConverter.cs` | 25 | win | TradingTerminal.UI | B I P | Y | Turns a 0..1 fraction into a star so a proportional bar fills |
| `src/windows/Shell/TradingTerminal.UI/Converters/Base64ToImageSourceConverter.cs` | 36 | win | TradingTerminal.UI | B I P | Y | Binds a base64-encoded PNG string to a WPF ImageSource. Returns null on |
| `src/windows/Shell/TradingTerminal.UI/Converters/BrokerKindConverters.cs` | 39 | win | TradingTerminal.UI | B I P | Y | Maps a |
| `src/windows/Shell/TradingTerminal.UI/Converters/CorrelationToBrushConverter.cs` | 58 | win | TradingTerminal.UI | B I P | Y | Maps a correlation value in [-1, 1] to a diverging heat colour: |
| `src/windows/Shell/TradingTerminal.UI/Converters/HaloBrushConverter.cs` | 48 | win | TradingTerminal.UI | B I P | Y | Converts a hex colour string (e.g. "#16C784") into a soft radial-gradient "halo" |
| `src/windows/Shell/TradingTerminal.UI/Converters/HexToSoftBrushConverter.cs` | 45 | win | TradingTerminal.UI | B I P | Y | Converts a hex colour string to a translucent — the same hue |
| `src/windows/Shell/TradingTerminal.UI/Converters/InstrumentTagsConverter.cs` | 98 | win | TradingTerminal.UI | B I P | Y | Projects a into the ordered list of coloured pills shown next to |
| `src/windows/Shell/TradingTerminal.UI/Converters/InverseBooleanConverter.cs` | 13 | win | TradingTerminal.UI | B I P | Y |  |
| `src/windows/Shell/TradingTerminal.UI/Converters/InverseBooleanToVisibilityConverter.cs` | 15 | win | TradingTerminal.UI | B I P | Y | public sealed class InverseBooleanToVisibilityConverter : IValueConverter |
| `src/windows/Shell/TradingTerminal.UI/Converters/ReferenceEqualsConverter.cs` | 20 | win | TradingTerminal.UI | B I P | Y | Multi-binding converter that returns true iff the two bound values are reference-equal. |
| `src/windows/Shell/TradingTerminal.UI/Converters/StrategyClassificationConverter.cs` | 141 | win | TradingTerminal.UI | B I P | Y | Projects an into the ordered list of coloured classification pills |
| `src/windows/Shell/TradingTerminal.UI/Converters/StrategyDataRequirementConverter.cs` | 113 | win | TradingTerminal.UI | B I P | Y | Projects a strategy's (or an |
| `src/windows/Shell/TradingTerminal.UI/Converters/StringToBrushConverter.cs` | 43 | win | TradingTerminal.UI | B I P | Y | Converts a hex colour string (e.g. "#E74C3C") to a . Lets a |
| `src/windows/Shell/TradingTerminal.UI/Converters/StringToVisibilityConverter.cs` | 15 | win | TradingTerminal.UI | B I P | Y | public sealed class StringToVisibilityConverter : IValueConverter |
| `src/windows/Shell/TradingTerminal.UI/Converters/UnsignedStrategyConverter.cs` | 44 | win | TradingTerminal.UI | B I P | Y | Registers a shared instance under |
| `src/windows/Shell/TradingTerminal.UI/CrashGuard.cs` | 142 | win | TradingTerminal.UI | B I P | Y | Directory the crash reports are written to. |
| `src/windows/Shell/TradingTerminal.UI/Diagnostics/PluginFaultWatchdog.cs` | 102 | win | TradingTerminal.UI | B I P | Y | Attaches to |
| `src/windows/Shell/TradingTerminal.UI/Diagnostics/StrategyWindowSmoke.cs` | 124 | win | TradingTerminal.UI | B I P | Y | Opens every strategy in |
| `src/windows/Shell/TradingTerminal.UI/InstrumentTag.cs` | 11 | win | TradingTerminal.UI | B I P | Y | One coloured pill rendered next to an instrument in the dropdowns — |
| `src/windows/Shell/TradingTerminal.UI/SimulatedDataState.cs` | 32 | win | TradingTerminal.UI | B I P | Y | True while the Simulated broker is among the connected set. |
| `src/windows/Shell/TradingTerminal.UI/Strategies/NullToVisibilityConverter.cs` | 21 | win | TradingTerminal.UI | B I P | Y | Collapses an element when its bound value is null or an empty/whitespace |
| `src/windows/Shell/TradingTerminal.UI/Strategies/ParameterTemplateSelector.cs` | 31 | win | TradingTerminal.UI | B I P | Y | Chooses the editor for a |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyCatalogItemViewModel.cs` | 59 | win | TradingTerminal.UI | B I P | Y | The underlying strategy — the catalog's pill converters, Open and Quick-backtest all |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyImageTile.xaml.cs` | 92 | win | TradingTerminal.UI | B I P | Y | Decoded once per process — every catalog row shares the one mark. |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyImageTile.xaml` | 49 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyParameterEditorView.xaml.cs` | 26 | win | TradingTerminal.UI | B I P | Y | Auto-generated parameter editor. Set its DataContext to a |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyParameterEditorView.xaml` | 81 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentation.cs` | 19 | win | TradingTerminal.UI | B I P | Y | User-authored presentation overrides for a strategy's catalog card — how it is |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditor.cs` | 23 | win | TradingTerminal.UI | B I P | Y | Shows the modal presentation editor for a catalog item; on Save it |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditorView.xaml.cs` | 12 | win | TradingTerminal.UI | B I P | Y | Modal editor for a strategy card's presentation overrides. The only code-behind is |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditorView.xaml` | 96 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationEditorViewModel.cs` | 85 | win | TradingTerminal.UI | B I P | Y | Clears every field back to the strategy's own compiled metadata (persisted as |
| `src/windows/Shell/TradingTerminal.UI/Strategies/StrategyPresentationStore.cs` | 81 | win | TradingTerminal.UI | B I P | Y | The overrides for a strategy, or |
| `src/windows/Shell/TradingTerminal.UI/StrategyChartHelpers.cs` | 33 | win | TradingTerminal.UI | B I P | Y | Apply the standard dark style to a ScottPlot WPF host. |
| `src/windows/Shell/TradingTerminal.UI/StrategyWindowBase.cs` | 129 | win | TradingTerminal.UI | B I P | Y | The WpfPlot hosts that should receive the dark-theme treatment. |
| `src/windows/Shell/TradingTerminal.UI/StrategyWindowPlacementStore.cs` | 128 | win | TradingTerminal.UI | B I P | Y | One strategy window's remembered placement: the normal (restore) bounds plus whether it |
| `src/windows/Shell/TradingTerminal.UI/Themes/AiStyles.xaml` | 221 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/Brushes.xaml` | 164 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/Components.xaml` | 254 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/Dark.xaml` | 406 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/GreekDark.xaml` | 163 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/GreekLight.xaml` | 176 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/Monochrome.xaml` | 158 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/StrategyShellStyles.xaml` | 117 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/TvDark.xaml` | 167 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Themes/TvLight.xaml` | 172 | win | TradingTerminal.UI | B I P | N | XAML |
| `src/windows/Shell/TradingTerminal.UI/Theming/ThemeManager.cs` | 446 | win | TradingTerminal.UI | B I P | Y | A selectable app theme — an id, a display name, and the |
| `src/windows/Shell/TradingTerminal.UI/Theming/ThemeToken.cs` | 58 | win | TradingTerminal.UI | B I P | Y | Whether a |
