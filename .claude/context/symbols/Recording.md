# TradingTerminal.Recording — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.Recording/RecorderEntry.cs
```cs
   18: public sealed partial class RecorderEntry : ObservableObject
   29: public RecorderEntry(SignalInstrument instrument, BrokerKind? pinnedBroker)
   35: public SignalInstrument Instrument { get; }
   38: public BrokerKind? PinnedBroker { get; }
   40: public string DisplayName => Instrument.DisplayName;
   41: public string Category => Instrument.Category;
   42: public string Symbol => Instrument.Contract.Symbol;
   45: public InstrumentId Id { get; internal set; }
   56: public long Quotes => Interlocked.Read(ref QuotesRaw);
   57: public long Trades => Interlocked.Read(ref TradesRaw);
   58: public long Bars => Interlocked.Read(ref BarsRaw);
   59: public long Depth => Interlocked.Read(ref DepthRaw);
   64: public bool SupportsTape => ActiveBroker is { } b && StrategyBrokerCapability.TapeBrokers.Contains(b);
   67: public bool SupportsDepth => ActiveBroker is { } b && StrategyBrokerCapability.DepthBrokers.Contains(b);
   72: public static bool SupportsL3 => false;
  111: public RecorderWatchlistItem ToWatchlistItem() => RecorderWatchlistItem.From(Instrument, PinnedBroker);
```

## src/windows/Tools/TradingTerminal.Recording/RecorderPanelView.xaml.cs
```cs
    5: public partial class RecorderPanelView : UserControl
    7: public RecorderPanelView()
```

## src/windows/Tools/TradingTerminal.Recording/RecorderPanelViewModel.cs
```cs
   23: public sealed partial class RecorderPanelViewModel : ViewModelBase, IDisposable
   33: public RecorderPanelViewModel(
   84: public TickRecordingService Service { get; }
   89: public ObservableCollection<SignalInstrument> Instruments { get; }
   91: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  141: public void Dispose()
```

## src/windows/Tools/TradingTerminal.Recording/RecorderWatchlistStore.cs
```cs
   19: public sealed record RecorderWatchlistItem(
   29: public static RecorderWatchlistItem From(SignalInstrument instrument, BrokerKind? pinned)
   38: public SignalInstrument ToInstrument() => new(
   44: public BrokerKind? PinnedBroker => Enum.TryParse<BrokerKind>(Broker, out var b) ? b : null;
   50: public sealed record RecorderWatchlist(
   55: public static RecorderWatchlist Empty { get; } = new(Array.Empty<RecorderWatchlistItem>(), false, false);
   64: public static class RecorderWatchlistStore
   72: public static RecorderWatchlist Load()
   89: public static void Save(RecorderWatchlist watchlist)
```

## src/windows/Tools/TradingTerminal.Recording/RecordingServiceCollectionExtensions.cs
```cs
    6: public static class RecordingServiceCollectionExtensions
    8: public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.Recording/TickRecordingService.cs
```cs
   34: public sealed partial class TickRecordingService : ObservableObject, IHostedService, IDisposable
   61: public TickRecordingService(
   83: public ObservableCollection<RecorderEntry> Instruments { get; } = new();
   99: public bool HasInstruments => Instruments.Count > 0;
  103: public Task StartAsync(CancellationToken cancellationToken)
  120: public Task StopAsync(CancellationToken cancellationToken)
  130: public void Add(SignalInstrument instrument)
  143: public void Remove(RecorderEntry entry)
  154: public void ToggleRecording()
  160: public void StartRecording()
  188: public void StopRecording(string reason)
  380: public void RefreshElapsed() =>
  388: public void Dispose()
```
