# TradingTerminal.QuantConnect — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.QuantConnect/AvaloniaUi/QuantConnectAvaloniaWindow.axaml.cs
```cs
   10: public partial class QuantConnectAvaloniaWindow : Window
   14: public QuantConnectAvaloniaWindow()
```

## src/linux/Tools/TradingTerminal.QuantConnect/LeanProcessRunner.cs
```cs
   10: public bool Success => ExitCode == 0;
   20: public static async Task<ProcessOutcome> RunAsync(
```

## src/linux/Tools/TradingTerminal.QuantConnect/LeanRuntimeSettings.cs
```cs
   11: public sealed class LeanRuntimeSettings
   13: public LeanEngineMode Mode { get; set; } = LeanEngineMode.LocalCli;
   14: public string CliPath { get; set; } = "";
   15: public string ProjectsFolder { get; set; } = "";
   16: public string DataFolder { get; set; } = "";
   17: public int RunTimeoutSeconds { get; set; } = 1800;
```

## src/linux/Tools/TradingTerminal.QuantConnect/LocalCliLeanClient.cs
```cs
   15: public sealed class LocalCliLeanClient : ILeanClient
   20: public LocalCliLeanClient(LeanRuntimeSettings settings, ILogger<LocalCliLeanClient> logger)
   26: public LeanEngineMode Mode => LeanEngineMode.LocalCli;
   32: public async Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default)
   45: public Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default)
   75: public async Task<LeanBacktestResult> RunBacktestAsync(
  100: public async Task<LeanDataResult> DownloadDataAsync(
```

## src/linux/Tools/TradingTerminal.QuantConnect/NullLeanClient.cs
```cs
   10: public sealed class NullLeanClient : ILeanClient
   12: public LeanEngineMode Mode { get; }
   16: public NullLeanClient(LeanEngineMode mode = LeanEngineMode.Cloud,
   23: public Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default) =>
   26: public Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default) =>
   29: public Task<LeanBacktestResult> RunBacktestAsync(
   33: public Task<LeanDataResult> DownloadDataAsync(
```

## src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectServiceCollectionExtensions.cs
```cs
   15: public static class QuantConnectServiceCollectionExtensions
   17: public static IServiceCollection AddQuantConnectSurface(this IServiceCollection services, IConfiguration configuration)
```

## src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectViewModel.cs
```cs
   25: public sealed partial class QuantConnectViewModel : ViewModelBase, IDisposable
   35: public QuantConnectViewModel(
   58: public ObservableCollection<LeanProject> Projects { get; }
   59: public ObservableCollection<LeanStatistic> Statistics { get; }
  249: public void Dispose()
```

## src/linux/Tools/TradingTerminal.QuantConnect/QuantConnectWindow.xaml.cs
```cs
   10: public partial class QuantConnectWindow : MetroWindow
   12: public QuantConnectWindow()
```
