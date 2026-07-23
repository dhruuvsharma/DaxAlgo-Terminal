# TradingTerminal.UI.Core — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/UI/TradingTerminal.UI.Core/BarIndicators.cs
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

## src/windows/UI/TradingTerminal.UI.Core/BrokerInstrumentUniverse.cs
```cs
   24: public static class BrokerInstrumentUniverse
   33: public static async Task<IReadOnlyList<SignalInstrument>> LoadAsync(
   66: public static string BrokerLabel(BrokerKind broker) => broker switch
   78: public static SignalInstrument? Reselect(
```

## src/windows/UI/TradingTerminal.UI.Core/BusyState.cs
```cs
   28: public sealed partial class BusyState : ObservableObject
   43: public IDisposable Begin(string title, string? message = null)
   53: public void Report(string message) => Message = message;
   67: public void Dispose()
```

## src/windows/UI/TradingTerminal.UI.Core/Catalog/StrategyCatalogViewModel.cs
```cs
   13: public sealed partial class StrategyCatalogViewModel : ObservableObject
   17: public StrategyCatalogViewModel(IEnumerable<BacktestStrategyOption> options, Action<string>? onLog = null)
   29: public ObservableCollection<StrategyCatalogItem> Items { get; }
   31: public int Count => Items.Count;
   37: public string Details => SelectedItem is { } s
   52: public sealed record StrategyCatalogItem(string Id, string DisplayName, int ParameterCount, bool Fast);
```

## src/windows/UI/TradingTerminal.UI.Core/Diagnostics/PluginFaultTracker.cs
```cs
   10: public sealed class PluginFaultTracker(int strikeLimit)
   16: public int StrikeLimit { get; } = strikeLimit;
   20: public (int Strikes, bool StruckOutNow) RecordFault(string plugin)
```

## src/windows/UI/TradingTerminal.UI.Core/ISignalGeneratorRouterFactory.cs
```cs
    9: public interface ISignalGeneratorRouterFactory
   11:     SignalGeneratorRouter Create();
   15: public sealed class SignalGeneratorRouterFactory : ISignalGeneratorRouterFactory
   17: public SignalGeneratorRouter Create() => new();
```

## src/windows/UI/TradingTerminal.UI.Core/InstrumentPickerFilter.cs
```cs
   20: public static class InstrumentPickerFilter
   26: public bool IsApplying { get; set; }
   27: public object? PendingDesired { get; set; }
   31: public static List<SignalInstrument> Visible(
   51: public static List<T> Visible<T>(
   71: public static List<SignalInstrument> Visible(
   91: public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
  166: public static SignalInstrument? Remembered(string key, IReadOnlyList<SignalInstrument> all)
  172: public static T? Remembered<T>(string key, IReadOnlyList<T> all, Func<T, string> symbolOf) where T : class
  184: public static SignalInstrument? InitialSelection(
  189: public static T? InitialSelection<T>(
```

## src/windows/UI/TradingTerminal.UI.Core/LastInstrumentStore.cs
```cs
   17: public static class LastInstrumentStore
   28: public static string? Load(string key)
   40: public static void Save(string key, string? symbol)
```

## src/windows/UI/TradingTerminal.UI.Core/LiveSignalStrategyViewModelBase.cs
```cs
   41: public abstract partial class LiveSignalStrategyViewModelBase : ViewModelBase, IDisposable
   43: public const int MaxSignalsRetained = 200;
   44: public const int MaxBarsRetained = 300;
   48: public const int MaxInstrumentsDisplayed = 500;
   58: protected virtual int WarmupBarCount => 120;
   87: protected LiveSignalStrategyViewModelBase(
  143: public string StrategyId { get; }
  144: public string StrategyDisplayName { get; }
  148: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  150: public ObservableCollection<SignalEntry> Signals { get; }
  153: public ObservableCollection<Bar> Bars { get; }
  155: public event EventHandler? BarsChanged;
  161: public event EventHandler? TickProcessed;
  187: protected virtual StrategyDataRequirement DataRequirement =>
  272: protected virtual void OnPauseReleased() { }
  391: protected abstract IBacktestStrategy BuildStrategy(Contract contract);
  395: protected virtual string? ValidateSetup() => null;
  399: protected virtual void OnBarsUpdated() { }
  405: protected virtual Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars) => Task.CompletedTask;
  409: protected void Log(string category, string message) =>
  857: public ObservableCollection<string> PresetNames { get; }
  872: protected virtual Dictionary<string, string>? CaptureExtraPreset() => null;
  876: protected virtual void ApplyExtraPreset(IReadOnlyDictionary<string, string> extras) { }
  968: public void Dispose()
```

## src/windows/UI/TradingTerminal.UI.Core/LiveStrategyHostServices.cs
```cs
   34: public sealed record LiveStrategyHostServices(
```

## src/windows/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs
```cs
   23: public sealed class InMemoryLogSink : INotifyPropertyChanged
   31: public static Action<Action> UiPost { get; set; } = static action => action();
   37: public InMemoryLogSink(int capacity = CapacityDefault)
   43: public int Capacity { get; }
   44: public ObservableCollection<LogEntry> Entries { get; }
   46: public event PropertyChangedEventHandler? PropertyChanged;
   48: public void Append(LogEntry entry)
   66: public void Append(string source, string level, string message) =>
   89: public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);
```

## src/windows/UI/TradingTerminal.UI.Core/Presets/StrategyViewPreset.cs
```cs
   13: public sealed record StrategyViewPreset(
```

## src/windows/UI/TradingTerminal.UI.Core/Presets/ToolPresetStore.cs
```cs
   16: public sealed class ToolPresetStore<T> where T : class
   24: public ToolPresetStore(string toolKey)
   32: public ToolPresetStore(string toolKey, string directory)
   39: public IReadOnlyList<string> Names
   47: public T? Get(string name)
   53: public void Save(string name, T preset)
   65: public bool Delete(string name)
```

## src/windows/UI/TradingTerminal.UI.Core/SignalEntry.cs
```cs
   10: public sealed record SignalEntry(
   19: public string SideText => Side == OrderSide.Buy ? "BUY" : "SELL";
   20: public string TypeText => OrderType.ToString();
   21: public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
```

## src/windows/UI/TradingTerminal.UI.Core/SignalGeneratorRouter.cs
```cs
   23: public sealed class SignalGeneratorRouter : IOrderRouter
   29: public IObservable<OrderEvent> OrderEvents => _events.AsObservable();
   31: public event Action<SignalEntry>? SignalEmitted;
   34: public void UpdateMarketContext(Tick tick) => _lastTick = tick;
   36: public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
   65: public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default)
```

## src/windows/UI/TradingTerminal.UI.Core/Strategies/ParameterEditorItem.cs
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

## src/windows/UI/TradingTerminal.UI.Core/Strategies/StrategyFactory.cs
```cs
   19: public sealed class StrategyFactory : IStrategyFactory
   26: public static Action<object, object> BindViewModel { get; set; } = DefaultBind;
   33: public StrategyFactory(
   43: public IReadOnlyList<ITradingStrategy> All
   48: public event EventHandler<StrategyCatalogChange>? Changed;
   50: public void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration)
   74: public StrategyHost Create(string strategyId)
```

## src/windows/UI/TradingTerminal.UI.Core/Strategies/StrategyParametersViewModel.cs
```cs
   19: public sealed partial class StrategyParametersViewModel : ObservableObject
   22: public static StrategyParametersViewModel FromSchema(StrategyParameterSchema schema) =>
   25: public StrategyParametersViewModel(StrategyParameters parameters)
   33: public StrategyParameters Parameters { get; }
   35: public ObservableCollection<ParameterEditorItem> Items { get; }
   37: public bool HasParameters => Items.Count > 0;
```

## src/windows/UI/TradingTerminal.UI.Core/TaskExtensions.cs
```cs
   10: public static class TaskExtensions
   13: public static void FireAndForgetSafe(this Task task, ILogger logger, string? context = null)
   24: public static void FireAndForgetSafe(this ValueTask task, ILogger logger, string? context = null)
```

## src/windows/UI/TradingTerminal.UI.Core/TradeableInstrument.cs
```cs
   15: public sealed record SignalInstrument(string DisplayName, string Category, Contract Contract, BrokerKind? Broker = null);
   30: public static class SignalInstrumentCatalog
   35: public static Func<IReadOnlyList<SignalInstrument>>? Source { get; set; }
   39: public static IReadOnlyList<SignalInstrument> All =>
   45: public static IReadOnlyList<SignalInstrument> FromRegistry(IInstrumentRegistry registry) =>
```

## src/windows/UI/TradingTerminal.UI.Core/UiFile.cs
```cs
    9: public static class UiFile
   14: public static Func<string, IReadOnlyList<string>, Task<string?>> OpenAsync { get; set; }
   19: public static Func<string, IReadOnlyList<string>, string, Task<string?>> SaveAsync { get; set; }
```

## src/windows/UI/TradingTerminal.UI.Core/UiThread.cs
```cs
   11: public static class UiThread
   17: public static Func<Func<Task>, Task> Marshal { get; set; } = static action => action();
   20: public static Task RunAsync(Func<Task> action) => Marshal(action);
   23: public static Task RunAsync(Action action) => Marshal(() => { action(); return Task.CompletedTask; });
   33: public static Func<TimeSpan, Action, IDisposable> CreateRenderTimer { get; set; } = DefaultRenderTimer;
   50: public CoalescingRenderTimer(TimeSpan interval, Action tick)
   85: public void Dispose()
```

## src/windows/UI/TradingTerminal.UI.Core/ViewModelBase.cs
```cs
    6: public abstract class ViewModelBase : ObservableObject
```
