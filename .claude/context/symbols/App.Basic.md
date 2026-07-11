# TradingTerminal.App.Basic — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Shell/TradingTerminal.App.Basic/App.xaml.cs
```cs
   22: public partial class App : Application
   26: public IServiceProvider Services => _host!.Services;
   28: public static new App Current => (App)Application.Current;
   30: protected override async void OnStartup(StartupEventArgs e)
  295: protected override async void OnExit(ExitEventArgs e)
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveActivityView.xaml.cs
```cs
    5: public partial class ArchiveActivityView : UserControl
    7: public ArchiveActivityView() => InitializeComponent();
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/ArchiveSettingsView.xaml.cs
```cs
   11: public partial class ArchiveSettingsView : UserControl
   15: public ArchiveSettingsView()
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramArchiveLogin.cs
```cs
   16: public sealed class TelegramArchiveLogin : ITelegramArchiveLogin
   23: public TelegramArchiveLogin(
   35: public bool IsConnected => _transport.IsReady;
   37: public TelegramArchiveCredentials Load()
   43: public async Task<TelegramArchiveLoginResult> ConnectAsync(
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramArchiveOptionsPostConfigure.cs
```cs
   15: public void PostConfigure(string? name, TelegramArchiveOptions options)
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/TelegramPromptDialog.xaml.cs
```cs
    6: public partial class TelegramPromptDialog : MetroWindow
    8: public TelegramPromptDialog(string headerText, string helpText)
   15: public string? InputValue => ((TelegramPromptDialogContext)DataContext).InputValue;
   34: public TelegramPromptDialogContext(string header, string help)
   40: public string HeaderText { get; }
   41: public string HelpText { get; }
   42: public string InputValue { get; set; } = string.Empty;
```

## src/windows/Shell/TradingTerminal.App.Basic/Archive/WpfTelegramAuthPrompt.cs
```cs
   12: public sealed class WpfTelegramAuthPrompt : ITelegramAuthPrompt
   14: public Task<string?> PromptAsync(string key, CancellationToken ct)
```

## src/windows/Shell/TradingTerminal.App.Basic/Authoring/StrategyAuthoringView.xaml.cs
```cs
   10: public partial class StrategyAuthoringView : UserControl
   12: public StrategyAuthoringView()
```

## src/windows/Shell/TradingTerminal.App.Basic/BrokerMetering/BrokerApiChipViewModel.cs
```cs
   11: public sealed partial class BrokerApiChipViewModel : ViewModelBase
   13: public BrokerApiChipViewModel(BrokerKind broker)
   19: public BrokerKind Broker { get; }
   22: public string Label { get; }
   35: public int AvailableCallsPerMinute => SoftLimitPerMinute > 0
   40: public BrokerApiChipStatus Status => SoftLimitPerMinute <= 0
   50: public string UsageDisplay => SoftLimitPerMinute > 0
   55: public string TooltipText => SoftLimitPerMinute > 0
   88: public enum BrokerApiChipStatus
```

## src/windows/Shell/TradingTerminal.App.Basic/BrokerMetering/BrokerApiMeterViewModel.cs
```cs
   18: public sealed partial class BrokerApiMeterViewModel : ViewModelBase, IDisposable
   24: public BrokerApiMeterViewModel(IBrokerApiMeter meter)
   36: public ObservableCollection<BrokerApiChipViewModel> Chips { get; }
   86: public void Dispose()
```

## src/windows/Shell/TradingTerminal.App.Basic/Composition/AppDependencyInjection.cs
```cs
   42: public static class AppDependencyInjection
   54: public static IServiceCollection AddCoreShell(this IServiceCollection services, IConfiguration configuration)
  106: public static IServiceCollection AddStrategyPlugins(this IServiceCollection services, IConfiguration configuration)
  187: public static IServiceCollection AddShell(this IServiceCollection services)
  214: public static IServiceCollection AddSupport(this IServiceCollection services)
  224: public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
  237: public static IServiceCollection AddArchiveSurface(this IServiceCollection services)
```

## src/windows/Shell/TradingTerminal.App.Basic/Logging/ObservableCollectionLogSink.cs
```cs
    8: public sealed class ObservableCollectionLogSink : ILogEventSink
   12: public ObservableCollectionLogSink(InMemoryLogSink ui) => _ui = ui;
   14: public void Emit(LogEvent logEvent)
```

## src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml.cs
```cs
    8: public partial class MainWindow : MetroWindow
   10: public MainWindow()
```

## src/windows/Shell/TradingTerminal.App.Basic/MainWindowViewModel.cs
```cs
   38: public sealed partial class MainWindowViewModel : ViewModelBase, IShellOverlayPresenter
   66: public MainWindowViewModel(
  207: public ObservableCollection<ITradingStrategy> Strategies { get; }
  208: public InMemoryLogSink LogSink { get; }
  213: public ICollectionView ActivityLog { get; }
  231: public BrokerApiMeterViewModel ApiMeter { get; }
  235: public int PluginProblemCount { get; }
  237: public bool HasPluginProblems => PluginProblemCount > 0;
  241: public string ModeDisplayName
  256: public bool IsLiveMode => _brokerSelector.Connected.Any(k => _brokerSelector.ModeOf(k).IsLive);
  258: public string ActiveBrokerLabel
  272: public string DisconnectBannerText => "Disconnected — connect a broker to resume";
  283: public bool IsAuthenticated => _session.IsAuthenticated;
  285: public string SessionUserDisplay
  342: public int ConnectedBrokerCount => _brokerSelector.Connected.Count;
  349: public bool IsDisconnected => ConnectionState is not Core.Domain.ConnectionState.Connected;
  358: public void OpenStrategy(string? strategyId)
  406: public void QuickBacktest(string? strategyId)
  445: public async Task ReconnectAsync()
  456: public void Exit()
  465: public async Task StartQuestDbAsync()
  475: public ObservableCollection<ThemeMenuOption> Themes { get; }
  502: public void OpenThemeStudio() =>
  534: public void OpenPluginManager() =>
  539: public void OpenBacktestStudio() =>
  543: public void OpenRecorder() =>
  547: public void OpenCorrelation() =>
  552: public void OpenLiveCorrelation() =>
  559: public void OpenSupport() =>
  564: public void OpenCharts() =>
  568: public void OpenOrderBook() =>
  572: public void OpenFootprint() =>
  576: public void OpenBookmap() =>
  580: public void OpenAdvancedRegime() =>
  586: public void OpenNotificationsSettings() =>
  590: public void OpenArchiveSettings() =>
  594: public void OpenArchiveActivity() => OpenOrActivateArchiveHistory();
  626: public void InstantOffload()
  633: public Task StartAsync()
```

## src/windows/Shell/TradingTerminal.App.Basic/Notifications/NotificationsSettingsView.xaml.cs
```cs
    5: public partial class NotificationsSettingsView : UserControl
    7: public NotificationsSettingsView()
```

## src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginManagerView.xaml.cs
```cs
    5: public partial class PluginManagerView : UserControl
    7: public PluginManagerView()
```

## src/windows/Shell/TradingTerminal.App.Basic/Plugins/PluginManagerViewModel.cs
```cs
   17: public sealed record PluginRow(
   35: public sealed partial class PluginManagerViewModel : ViewModelBase
   40: public PluginManagerViewModel(PluginHostContext context)
   59: public string PluginsRoot { get; }
   60: public string TrustPolicySummary { get; }
   61: public ObservableCollection<PluginRow> Rows { get; } = new();
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/IShellFactory.cs
```cs
   14: public interface ILoginShellFactory
   18:     Window Create(EventHandler<bool> onCompleted);
   21: public interface IMainShellFactory
   24:     Window Create();
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/IShellWindowHost.cs
```cs
   12: public interface IShellWindowHost
   16:     IShellOverlayPresenter? OverlayPresenter { get; set; }
   19:     bool TryActivate(string windowId);
   22:     bool IsOpen(string windowId);
   25:     void Register(string windowId, Window window);
   28:     void Unregister(string windowId);
   33:     void OpenWithOverlay(string title, string detail, Action build);
   37:     void OpenHostedTool<TVm, TView>(string windowId, string title, string detail)
   38:     where TVm : class
   39:     where TView : FrameworkElement;
   43:     void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
   44:     where TVm : class
   45:     where TWindow : Window;
   52: public interface IShellOverlayPresenter
   55:     void Show(string title, string detail);
   58:     void Hide();
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/LoginShellFactory.cs
```cs
   11: public LoginShellFactory(IServiceProvider services) => _services = services;
   13: public Window Create(EventHandler<bool> onCompleted)
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/MainShellFactory.cs
```cs
   10: public MainShellFactory(IServiceProvider services) => _services = services;
   12: public Window Create()
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/ShellWindowHost.cs
```cs
   20: public ShellWindowHost(IServiceProvider services, ILogger<ShellWindowHost> logger)
   26: public IShellOverlayPresenter? OverlayPresenter { get; set; }
   28: public bool TryActivate(string windowId)
   34: public bool IsOpen(string windowId) => _openWindows.ContainsKey(windowId);
   36: public void Register(string windowId, Window window) => _openWindows[windowId] = window;
   38: public void Unregister(string windowId) => _openWindows.Remove(windowId);
   40: public void OpenWithOverlay(string title, string detail, Action build)
   52: public void OpenHostedTool<TVm, TView>(string windowId, string title, string detail)
   77: public void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/ThemeMenuOption.cs
```cs
    7: public sealed partial class ThemeMenuOption : ObservableObject
    9: public ThemeMenuOption(string id, string name)
   15: public string Id { get; }
   16: public string Name { get; }
```

## src/windows/Shell/TradingTerminal.App.Basic/Shell/ToolHostWindow.cs
```cs
   14: public sealed class ToolHostWindow : MetroWindow
   19: public static ToolHostWindow Create(string title, FrameworkElement content)
```

## src/windows/Shell/TradingTerminal.App.Basic/Support/ISupportPrompt.cs
```cs
   10: public interface ISupportPrompt
   15:     void MaybeShowOnLaunch(Window owner);
   18:     void Show(Window owner);
```

## src/windows/Shell/TradingTerminal.App.Basic/Support/SupportPrompt.cs
```cs
   28: public SupportPrompt(IServiceProvider services, ILogger<SupportPrompt> logger)
   34: public void MaybeShowOnLaunch(Window owner)
   58: public void Show(Window owner)
```

## src/windows/Shell/TradingTerminal.App.Basic/Support/SupportWindow.xaml.cs
```cs
   11: public partial class SupportWindow : MetroWindow
   15: public SupportWindow()
```

## src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeStudioView.xaml.cs
```cs
    5: public partial class ThemeStudioView : UserControl
    7: public ThemeStudioView()
```

## src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeStudioViewModel.cs
```cs
   16: public sealed partial class ThemeStudioViewModel : ViewModelBase
   27: public ThemeStudioViewModel(IThemeManager manager)
   40: public ObservableCollection<ThemeDefinition> BaseThemes { get; }
   41: public ObservableCollection<ThemeTokenGroupViewModel> Groups { get; }
  198: public sealed class ThemeTokenGroupViewModel
  200: public ThemeTokenGroupViewModel(string name) => Name = name;
  202: public string Name { get; }
  203: public ObservableCollection<ThemeTokenViewModel> Tokens { get; } = new();
```

## src/windows/Shell/TradingTerminal.App.Basic/Theming/ThemeTokenViewModel.cs
```cs
   13: public sealed class ThemeTokenViewModel : ObservableObject
   18: public ThemeTokenViewModel(IThemeManager manager, ThemeToken token)
   38: public string DisplayName { get; }
   39: public string PrimaryKey { get; }
   40: public string? LinkedColorKey { get; }
   41: public bool IsGradient { get; }
   42: public bool IsSolid => !IsGradient;
   44: public ObservableCollection<GradientStopViewModel> Stops { get; }
   49: public Color Color => _color;
   52: public Brush Swatch => new SolidColorBrush(_color);
   54: public string Hex
   64: public byte A { get => _color.A; set => SetChannel(value, (c, v) => Color.FromArgb(v, c.R, c.G, c.B)); }
   65: public byte R { get => _color.R; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, v, c.G, c.B)); }
   66: public byte G { get => _color.G; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, v, c.B)); }
   67: public byte B { get => _color.B; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, c.G, v)); }
   96: public Brush GradientPreview
  116: public sealed class GradientStopViewModel : ObservableObject
  122: public GradientStopViewModel(int index, Color color, Action onChanged)
  129: public int Index { get; }
  130: public string Label => $"Stop {Index + 1}";
  131: public Color Color => _color;
  132: public Brush Swatch => new SolidColorBrush(_color);
  134: public string Hex
  144: public byte A { get => _color.A; set => SetChannel(value, (c, v) => Color.FromArgb(v, c.R, c.G, c.B)); }
  145: public byte R { get => _color.R; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, v, c.G, c.B)); }
  146: public byte G { get => _color.G; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, v, c.B)); }
  147: public byte B { get => _color.B; set => SetChannel(value, (c, v) => Color.FromArgb(c.A, c.R, c.G, v)); }
  175: public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
  177: public static bool TryParse(string? s, out Color c)
```
