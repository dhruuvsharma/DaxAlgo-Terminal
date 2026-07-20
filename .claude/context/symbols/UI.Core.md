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
   14: public sealed partial class StrategyCatalogViewModel : ObservableObject
   18: public StrategyCatalogViewModel(IEnumerable<BacktestStrategyOption> options, Action<string>? onLog = null)
   30: public ObservableCollection<StrategyCatalogItem> Items { get; }
   32: public int Count => Items.Count;
   38: public string Details => SelectedItem is { } s
   53: public sealed record StrategyCatalogItem(string Id, string DisplayName, int ParameterCount, bool Fast);
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
   19: public static class InstrumentPickerFilter
   22: public static List<SignalInstrument> Visible(
   42: public static List<T> Visible<T>(
   62: public static List<SignalInstrument> Visible(
   82: public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
   99: public static SignalInstrument? Remembered(string key, IReadOnlyList<SignalInstrument> all)
  105: public static T? Remembered<T>(string key, IReadOnlyList<T> all, Func<T, string> symbolOf) where T : class
  117: public static SignalInstrument? InitialSelection(
  122: public static T? InitialSelection<T>(
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
   86: protected LiveSignalStrategyViewModelBase(
  142: public string StrategyId { get; }
  143: public string StrategyDisplayName { get; }
  147: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  149: public ObservableCollection<SignalEntry> Signals { get; }
  152: public ObservableCollection<Bar> Bars { get; }
  154: public event EventHandler? BarsChanged;
  160: public event EventHandler? TickProcessed;
  186: protected virtual StrategyDataRequirement DataRequirement =>
  271: protected virtual void OnPauseReleased() { }
  390: protected abstract IBacktestStrategy BuildStrategy(Contract contract);
  394: protected virtual string? ValidateSetup() => null;
  398: protected virtual void OnBarsUpdated() { }
  404: protected virtual Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars) => Task.CompletedTask;
  408: protected void Log(string category, string message) =>
  818: public ObservableCollection<string> PresetNames { get; }
  833: protected virtual Dictionary<string, string>? CaptureExtraPreset() => null;
  837: protected virtual void ApplyExtraPreset(IReadOnlyDictionary<string, string> extras) { }
  929: public void Dispose()
```

## src/windows/UI/TradingTerminal.UI.Core/LiveStrategyHostServices.cs
```cs
   34: public sealed record LiveStrategyHostServices(
```

## src/windows/UI/TradingTerminal.UI.Core/Logging/InMemoryLogSink.cs
```cs
   24: public sealed class InMemoryLogSink : INotifyPropertyChanged
   33: public static Action<Action> UiPost { get; set; } = static action => action();
   39: public InMemoryLogSink(int capacity = CapacityDefault)
   45: public int Capacity { get; }
   46: public ObservableCollection<LogEntry> Entries { get; }
   48: public event PropertyChangedEventHandler? PropertyChanged;
   50: public void Append(LogEntry entry)
   68: public void Append(string source, string level, string message) =>
   91: public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);
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
   20: public sealed class StrategyFactory : IStrategyFactory
   28: public static Action<object, object> BindViewModel { get; set; } = DefaultBind;
   35: public StrategyFactory(
   45: public IReadOnlyList<ITradingStrategy> All
   50: public event EventHandler<StrategyCatalogChange>? Changed;
   52: public void Register(ITradingStrategy strategy, StrategyFactoryRegistration registration)
   76: public StrategyHost Create(string strategyId)
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
   10: public static class UiFile
   15: public static Func<string, IReadOnlyList<string>, Task<string?>> OpenAsync { get; set; }
   20: public static Func<string, IReadOnlyList<string>, string, Task<string?>> SaveAsync { get; set; }
```

## src/windows/UI/TradingTerminal.UI.Core/UiThread.cs
```cs
   11: public static class UiThread
   17: public static Func<Func<Task>, Task> Marshal { get; set; } = static action => action();
   20: public static Task RunAsync(Func<Task> action) => Marshal(action);
   23: public static Task RunAsync(Action action) => Marshal(() => { action(); return Task.CompletedTask; });
   33: public static Func<TimeSpan, Action, IDisposable> CreateRenderTimer { get; set; } = DefaultRenderTimer;
   48: public void Dispose() => timer.Dispose();
```

## src/windows/UI/TradingTerminal.UI.Core/ViewModelBase.cs
```cs
    6: public abstract class ViewModelBase : ObservableObject
```
