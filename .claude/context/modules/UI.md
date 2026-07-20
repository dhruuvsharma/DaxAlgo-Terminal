# TradingTerminal.UI — themes + shared WPF controls

**Path** `src/windows/Shell/TradingTerminal.UI/` · **Editions** B I P · **Blast: high**

**Purpose.** Shared WPF layer ABOVE UI.Core: MahApps dark theme + `ThemeManager` (,
custom-theme JSON import/export for Theme Studio), reusable controls (`InstrumentPicker`,
`BusyOverlay`, `BannerHost`, `SimulatedDataBanner`/`SimulatedDataState`, param controls),
value-converter suite, `CrashGuard`.

**Split vs UI.Core (matters!).** WPF-heavy chrome/theming lives HERE; the strategy-VM base classes,
`LiveStrategyHostServices`, and `InMemoryLogSink` live in **TradingTerminal.UI.Core**
(`src/windows/UI/`). CLAUDE.md's older map says "UI" for those — trust this layer.

**Depends on** Core, UI.Core. **Depended by** all chart/tool projects, Login, Sdk.Wpf, both shells.

**Surface** `symbols/UI.md` (367 lines). **Tests** Tests ([WpfFact]) + Tests.Headless.

**Invariants.** Strict MVVM (no logic in code-behind); theme tokens via IThemeManager, no hard-coded
brushes in consumers; controls must be edition-agnostic.

**Common changes.** New shared control (check both shells pick it up); theme token additions
(Theme Studio compatibility); converter additions. Load `wpf-mvvm-rules`; `xaml-fixer` agent for
binding/brush bugs.
