# TradingTerminal.UI.Core — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/UI/TradingTerminal.UI.Core/BarIndicators.cs
```cs
   14: public static class BarIndicators
   16: public static double[] Sma(IReadOnlyList<Bar> bars, int period)
   29: public static double[] Ema(IReadOnlyList<Bar> bars, int period)
   43: public static (double[] mean, double[] sd, double[] upper, double[] lower) Bollinger(
   66: public static double[] Rsi(IReadOnlyList<Bar> bars, int period)
   79: public static (double[] macd, double[] signal) Macd(
  102: public static double[] ZScore(IReadOnlyList<Bar> bars, int window)
  118: public static double[] RealisedVol(IReadOnlyList<Bar> bars, int window)
  132: public static double[] Atr(IReadOnlyList<Bar> bars, int period)
  144: public static double[] BarTimestamps(IReadOnlyList<Bar> bars)
  151: public static double[] BarCloses(IReadOnlyList<Bar> bars)
```

## src/linux/UI/TradingTerminal.UI.Core/BusyState.cs
```cs
   28: public sealed partial class BusyState : ObservableObject
   43: public IDisposable Begin(string title, string? message = null)
   53: public void Report(string message) => Message = message;
   67: public void Dispose()
```

## src/linux/UI/TradingTerminal.UI.Core/Catalog/StrategyCatalogViewModel.cs
```cs
   14: public sealed partial class StrategyCatalogViewModel : ObservableObject
   18: public StrategyCatalogViewModel(IEnumerable<BacktestStrategyOption> options, Action<string>? onLog = null)
   30: public ObservableCollection<StrategyCatalogItem> Items { get; }
   32: public int Count => Items.Count;
   38: public string Details => SelectedItem is { } s
   53: public sealed record StrategyCatalogItem(string Id, string DisplayName, int ParameterCount, bool Fast);
```

## src/linux/UI/TradingTerminal.UI.Core/ISignalGeneratorRouterFactory.cs
```cs
    9: public interface ISignalGeneratorRouterFactory
   11:     SignalGeneratorRouter Create();
   15: public sealed class SignalGeneratorRouterFactory : ISignalGeneratorRouterFactory
   17: public SignalGeneratorRouter Create() => new();
```

## src/linux/UI/TradingTerminal.UI.Core/LiveSignalStrategyViewModelBase.cs
```cs
   38: public abstract partial class LiveSignalStrategyViewModelBase : ViewModelBase, IDisposable
   40: public const int MaxSignalsRetained = 200;
   41: public const int MaxBarsRetained = 300;
   45: public const int MaxInstrumentsDisplayed = 500;
   55: protected virtual int WarmupBarCount => 120;
   83: protected LiveSignalStrategyViewModelBase(
  133: public string StrategyId { get; }
  134: public string StrategyDisplayName { get; }
  138: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  140: public ObservableCollection<SignalEntry> Signals { get; }
  143: public ObservableCollection<Bar> Bars { get; }
  145: public event EventHandler? BarsChanged;
  151: public event EventHandler? TickProcessed;
  177: protected virtual StrategyDataRequirement DataRequirement =>
  344: protected abstract IBacktestStrategy BuildStrategy(Contract contract);
  348: protected virtual string? ValidateSetup() => null;
  352: protected virtual void OnBarsUpdated() { }
  358: protected virtual Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars) => Task.CompletedTask;
  362: protected void Log(string category, string message) =>
  767: public void Dispose()
```

## src/linux/UI/TradingTerminal.UI.Core/LiveStrategyHostServices.cs
```cs
   34: public sealed record LiveStrategyHostServices(
```

## src/linux/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs
```cs
   19: public sealed class InMemoryLogSink : INotifyPropertyChanged
   28: public static Action<Action> UiPost { get; set; } = static action => action();
   30: public InMemoryLogSink(int capacity = CapacityDefault)
   36: public int Capacity { get; }
   37: public ObservableCollection<LogEntry> Entries { get; }
   39: public event PropertyChangedEventHandler? PropertyChanged;
   41: public void Append(LogEntry entry) => UiPost(() => DoAppend(entry));
   46: public void Append(string source, string level, string message) =>
   59: public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);
```

## src/linux/UI/TradingTerminal.UI.Core/SignalEntry.cs
```cs
   10: public sealed record SignalEntry(
   19: public string SideText => Side == OrderSide.Buy ? "BUY" : "SELL";
   20: public string TypeText => OrderType.ToString();
   21: public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
```

## src/linux/UI/TradingTerminal.UI.Core/SignalGeneratorRouter.cs
```cs
   23: public sealed class SignalGeneratorRouter : IOrderRouter
   29: public IObservable<OrderEvent> OrderEvents => _events.AsObservable();
   31: public event Action<SignalEntry>? SignalEmitted;
   34: public void UpdateMarketContext(Tick tick) => _lastTick = tick;
   36: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
   65: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
```

## src/linux/UI/TradingTerminal.UI.Core/Strategies/ParameterEditorItem.cs
```cs
   15: public sealed class ParameterEditorItem : ObservableObject
   17: public ParameterEditorItem(StrategyParameters bag, StrategyParameter parameter)
   25: public StrategyParameter Parameter { get; }
   27: public string Key => Parameter.Key;
   28: public string DisplayName => Parameter.DisplayName;
   29: public string? Description => Parameter.Description;
   30: public string? Group => Parameter.Group;
   31: public string? Unit => Parameter.Unit;
   32: public ParameterKind Kind => Parameter.Kind;
   34: public bool HasRange => Parameter.Min.HasValue && Parameter.Max.HasValue;
   35: public double Min => Parameter.Min ?? double.MinValue;
   36: public double Max => Parameter.Max ?? double.MaxValue;
   37: public double Step => Parameter.Step ?? (Kind == ParameterKind.Integer ? 1 : 0.1);
   38: public bool IsInteger => Kind == ParameterKind.Integer;
   39: public IReadOnlyList<string> Choices => Parameter.Choices ?? Array.Empty<string>();
   42: public double NumberValue
   53: public bool BoolValue
   59: public string TextValue
   66: public string SelectedChoice
   73: public string DisplayValue => Kind switch
   84: public void Refresh()
```

## src/linux/UI/TradingTerminal.UI.Core/Strategies/StrategyFactory.cs
```cs
   14: public sealed class StrategyFactory : IStrategyFactory
   22: public static Action<object, object> BindViewModel { get; set; } = DefaultBind;
   27: public StrategyFactory(
   37: public IReadOnlyList<ITradingStrategy> All { get; }
   39: public StrategyHost Create(string strategyId)
```

## src/linux/UI/TradingTerminal.UI.Core/Strategies/StrategyParametersViewModel.cs
```cs
   19: public sealed partial class StrategyParametersViewModel : ObservableObject
   22: public static StrategyParametersViewModel FromSchema(StrategyParameterSchema schema) =>
   25: public StrategyParametersViewModel(StrategyParameters parameters)
   33: public StrategyParameters Parameters { get; }
   35: public ObservableCollection<ParameterEditorItem> Items { get; }
   37: public bool HasParameters => Items.Count > 0;
```

## src/linux/UI/TradingTerminal.UI.Core/TaskExtensions.cs
```cs
   10: public static class TaskExtensions
   13: public static void FireAndForgetSafe(this Task task, ILogger logger, string? context = null)
   24: public static void FireAndForgetSafe(this ValueTask task, ILogger logger, string? context = null)
```

## src/linux/UI/TradingTerminal.UI.Core/TradeableInstrument.cs
```cs
   15: public sealed record SignalInstrument(string DisplayName, string Category, Contract Contract, BrokerKind? Broker = null);
   30: public static class SignalInstrumentCatalog
   35: public static Func<IReadOnlyList<SignalInstrument>>? Source { get; set; }
   39: public static IReadOnlyList<SignalInstrument> All =>
   45: public static IReadOnlyList<SignalInstrument> FromRegistry(IInstrumentRegistry registry) =>
```

## src/linux/UI/TradingTerminal.UI.Core/UiFile.cs
```cs
   10: public static class UiFile
   15: public static Func<string, IReadOnlyList<string>, Task<string?>> OpenAsync { get; set; }
   20: public static Func<string, IReadOnlyList<string>, string, Task<string?>> SaveAsync { get; set; }
```

## src/linux/UI/TradingTerminal.UI.Core/UiThread.cs
```cs
   11: public static class UiThread
   17: public static Func<Func<Task>, Task> Marshal { get; set; } = static action => action();
   20: public static Task RunAsync(Func<Task> action) => Marshal(action);
   23: public static Task RunAsync(Action action) => Marshal(() => { action(); return Task.CompletedTask; });
   33: public static Func<TimeSpan, Action, IDisposable> CreateRenderTimer { get; set; } = DefaultRenderTimer;
   48: public void Dispose() => timer.Dispose();
```

## src/linux/UI/TradingTerminal.UI.Core/ViewModelBase.cs
```cs
    6: public abstract class ViewModelBase : ObservableObject
```
