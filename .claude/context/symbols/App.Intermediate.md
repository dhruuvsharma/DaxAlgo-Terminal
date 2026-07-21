# TradingTerminal.App.Intermediate — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Shell/TradingTerminal.App.Intermediate/App.xaml.cs
```cs
   23: public partial class App : Application
   27: public IServiceProvider Services => _host!.Services;
   29: public static new App Current => (App)Application.Current;
   31: protected override async void OnStartup(StartupEventArgs e)
  312: protected override async void OnExit(ExitEventArgs e)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveActivityView.xaml.cs
```cs
    5: public partial class ArchiveActivityView : UserControl
    7: public ArchiveActivityView() => InitializeComponent();
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/ArchiveSettingsView.xaml.cs
```cs
   11: public partial class ArchiveSettingsView : UserControl
   15: public ArchiveSettingsView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramArchiveLogin.cs
```cs
   16: public sealed class TelegramArchiveLogin : ITelegramArchiveLogin
   23: public TelegramArchiveLogin(
   35: public bool IsConnected => _transport.IsReady;
   37: public TelegramArchiveCredentials Load()
   43: public async Task<TelegramArchiveLoginResult> ConnectAsync(
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramArchiveOptionsPostConfigure.cs
```cs
   15: public void PostConfigure(string? name, TelegramArchiveOptions options)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/TelegramPromptDialog.xaml.cs
```cs
    6: public partial class TelegramPromptDialog : MetroWindow
    8: public TelegramPromptDialog(string headerText, string helpText)
   15: public string? InputValue => ((TelegramPromptDialogContext)DataContext).InputValue;
   34: public TelegramPromptDialogContext(string header, string help)
   40: public string HeaderText { get; }
   41: public string HelpText { get; }
   42: public string InputValue { get; set; } = string.Empty;
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Archive/WpfTelegramAuthPrompt.cs
```cs
   12: public sealed class WpfTelegramAuthPrompt : ITelegramAuthPrompt
   14: public Task<string?> PromptAsync(string key, CancellationToken ct)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/AiProvidersSettingsView.xaml.cs
```cs
    5: public partial class AiProvidersSettingsView : UserControl
    7: public AiProvidersSettingsView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Authoring/StrategyAuthoringView.xaml.cs
```cs
   13: public partial class StrategyAuthoringView : UserControl
   17: public StrategyAuthoringView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/BrokerMetering/BrokerApiChipViewModel.cs
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

## src/windows/Shell/TradingTerminal.App.Intermediate/BrokerMetering/BrokerApiMeterViewModel.cs
```cs
   18: public sealed partial class BrokerApiMeterViewModel : ViewModelBase, IDisposable
   24: public BrokerApiMeterViewModel(IBrokerApiMeter meter)
   36: public ObservableCollection<BrokerApiChipViewModel> Chips { get; }
   86: public void Dispose()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Composition/AppDependencyInjection.cs
```cs
   45: public static class AppDependencyInjection
   57: public static IServiceCollection AddCoreShell(this IServiceCollection services, IConfiguration configuration)
  109: public static IServiceCollection AddStrategyPlugins(this IServiceCollection services, IConfiguration configuration)
  210: public static IServiceCollection AddShell(this IServiceCollection services)
  237: public static IServiceCollection AddSupport(this IServiceCollection services)
  247: public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
  262: public static IServiceCollection AddArchiveSurface(this IServiceCollection services)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Logging/ObservableCollectionLogSink.cs
```cs
    8: public sealed class ObservableCollectionLogSink : ILogEventSink
   12: public ObservableCollectionLogSink(InMemoryLogSink ui) => _ui = ui;
   14: public void Emit(LogEvent logEvent)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/MainWindow.xaml.cs
```cs
   10: public partial class MainWindow : MetroWindow
   12: public MainWindow()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/MainWindowViewModel.cs
```cs
   41: public sealed partial class MainWindowViewModel : ViewModelBase, IShellOverlayPresenter
   72: public MainWindowViewModel(
  238: public ObservableCollection<ITradingStrategy> Strategies { get; }
  243: public ObservableCollection<StrategyCatalogItemViewModel> CatalogItems { get; }
  247: public System.Collections.Generic.IReadOnlySet<string> UnsignedStrategyIds { get; }
  248: public InMemoryLogSink LogSink { get; }
  253: public ICollectionView ActivityLog { get; }
  271: public BrokerApiMeterViewModel ApiMeter { get; }
  275: public int PluginProblemCount { get; }
  277: public bool HasPluginProblems => PluginProblemCount > 0;
  281: public string ModeDisplayName
  296: public bool IsLiveMode => _brokerSelector.Connected.Any(k => _brokerSelector.ModeOf(k).IsLive);
  298: public string ActiveBrokerLabel
  312: public string DisconnectBannerText => "Disconnected — connect a broker to resume";
  323: public bool IsAuthenticated => _session.IsAuthenticated;
  325: public string SessionUserDisplay
  400: public int ConnectedBrokerCount => _brokerSelector.Connected.Count;
  407: public bool IsDisconnected => ConnectionState is not Core.Domain.ConnectionState.Connected;
  416: public void OpenStrategy(string? strategyId)
  466: public void QuickBacktest(string? strategyId)
  505: public async Task ReconnectAsync()
  516: public void Exit()
  525: public async Task StartQuestDbAsync()
  535: public ObservableCollection<ThemeMenuOption> Themes { get; }
  562: public void OpenThemeStudio() =>
  594: public void OpenPluginManager() =>
  599: public void OpenStrategyAuthoring() =>
  605: public IReadOnlyList<CliLaunchChoice> CliLaunchChoices { get; }
  609: public bool HasCliLaunchers => CliLaunchChoices.Any(choice => choice.IsAvailable);
  615: public void LaunchCli(CliLaunchChoice? choice)
  631: public void OpenBacktestStudio() =>
  637: public TickRecordingService Recorder { get; }
  642: public void OpenRecorder() =>
  647: public void OpenCorrelation() =>
  652: public void OpenLiveCorrelation() =>
  659: public void OpenSupport() =>
  664: public void OpenCharts() =>
  668: public void OpenOrderBook() =>
  672: public void OpenFootprint() =>
  676: public void OpenBookmap() =>
  680: public void OpenAdvancedRegime() =>
  686: public void OpenNotificationsSettings() =>
  690: public void OpenAiProvidersSettings() =>
  695: public void OpenArchiveSettings() =>
  699: public void OpenArchiveActivity() => OpenOrActivateArchiveHistory();
  731: public void InstantOffload()
  738: public Task StartAsync()
  750: public sealed class CliLaunchChoice(AgentCliAdapter adapter, bool isAvailable)
  752: public AgentCliAdapter Adapter { get; } = adapter;
  753: public bool IsAvailable { get; } = isAvailable;
  754: public string DisplayName => Adapter.DisplayName;
  755: public string MenuHeader => IsAvailable ? Adapter.DisplayName : $"{Adapter.DisplayName} — not installed";
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Notifications/NotificationsSettingsView.xaml.cs
```cs
    5: public partial class NotificationsSettingsView : UserControl
    7: public NotificationsSettingsView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginConsentDialog.xaml.cs
```cs
   16: public partial class PluginConsentDialog : MetroWindow
   34: public string Headline { get; }
   35: public string PublisherText { get; }
   36: public string PathText { get; }
   37: public string HashText { get; }
   38: public IReadOnlyList<string> Capabilities { get; }
   42: public static bool Ask(PluginConsentRequest request)
   80: public sealed class PluginConsentPrompt : IPluginConsentPrompt
   82: public bool RequestConsent(PluginConsentRequest request) => PluginConsentDialog.Ask(request);
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginManagerView.xaml.cs
```cs
    5: public partial class PluginManagerView : UserControl
    7: public PluginManagerView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Plugins/PluginManagerViewModel.cs
```cs
   21: public sealed record PluginRow(
   45: public sealed partial class PluginManagerViewModel : ViewModelBase
   53: public PluginManagerViewModel(PluginHostContext context, PluginFeedClient feed, IHttpClientFactory httpFactory)
   83: public string PluginsRoot { get; }
   84: public string TrustPolicySummary { get; }
   85: public ObservableCollection<PluginRow> Rows { get; } = new();
   96: public ObservableCollection<PluginCatalogItem> CatalogItems { get; } = new();
   99: public bool FeedConfigured => _feed.IsConfigured;
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/IShellFactory.cs
```cs
   14: public interface ILoginShellFactory
   18:     Window Create(EventHandler<bool> onCompleted);
   21: public interface IMainShellFactory
   24:     Window Create();
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/IShellWindowHost.cs
```cs
   12: public interface IShellWindowHost
   16:     IShellOverlayPresenter? OverlayPresenter { get; set; }
   19:     bool TryActivate(string windowId);
   22:     bool IsOpen(string windowId);
   25:     void Register(string windowId, Window window);
   28:     void Unregister(string windowId);
   33:     void OpenWithOverlay(string title, string detail, Action build);
   37:     void OpenHostedTool<TVm, TView>(string windowId, string title, string detail,
   38:     double width = ToolHostWindow.DefaultWidth, double height = ToolHostWindow.DefaultHeight)
   39:     where TVm : class
   40:     where TView : FrameworkElement;
   44:     void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
   45:     where TVm : class
   46:     where TWindow : Window;
   53: public interface IShellOverlayPresenter
   56:     void Show(string title, string detail);
   59:     void Hide();
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/LoginShellFactory.cs
```cs
   11: public LoginShellFactory(IServiceProvider services) => _services = services;
   13: public Window Create(EventHandler<bool> onCompleted)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/MainShellFactory.cs
```cs
   10: public MainShellFactory(IServiceProvider services) => _services = services;
   12: public Window Create()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ShellWindowHost.cs
```cs
   20: public ShellWindowHost(IServiceProvider services, ILogger<ShellWindowHost> logger)
   26: public IShellOverlayPresenter? OverlayPresenter { get; set; }
   28: public bool TryActivate(string windowId)
   34: public bool IsOpen(string windowId) => _openWindows.ContainsKey(windowId);
   36: public void Register(string windowId, Window window) => _openWindows[windowId] = window;
   38: public void Unregister(string windowId) => _openWindows.Remove(windowId);
   40: public void OpenWithOverlay(string title, string detail, Action build)
   52: public void OpenHostedTool<TVm, TView>(string windowId, string title, string detail,
   78: public void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ThemeMenuOption.cs
```cs
    7: public sealed partial class ThemeMenuOption : ObservableObject
    9: public ThemeMenuOption(string id, string name)
   15: public string Id { get; }
   16: public string Name { get; }
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Shell/ToolHostWindow.cs
```cs
   14: public sealed class ToolHostWindow : MetroWindow
   21: public const double DefaultWidth = 1100;
   22: public const double DefaultHeight = 760;
   24: public static ToolHostWindow Create(string title, FrameworkElement content,
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Support/ISupportPrompt.cs
```cs
   10: public interface ISupportPrompt
   15:     void MaybeShowOnLaunch(Window owner);
   18:     void Show(Window owner);
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Support/SupportPrompt.cs
```cs
   28: public SupportPrompt(IServiceProvider services, ILogger<SupportPrompt> logger)
   34: public void MaybeShowOnLaunch(Window owner)
   58: public void Show(Window owner)
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Support/SupportWindow.xaml.cs
```cs
   11: public partial class SupportWindow : MetroWindow
   15: public SupportWindow()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeStudioView.xaml.cs
```cs
    5: public partial class ThemeStudioView : UserControl
    7: public ThemeStudioView()
```

## src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeStudioViewModel.cs
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

## src/windows/Shell/TradingTerminal.App.Intermediate/Theming/ThemeTokenViewModel.cs
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
