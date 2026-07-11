# TradingTerminal.Recording — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.Recording/AvaloniaUi/TickRecorderAvaloniaWindow.axaml.cs
```cs
    7: public partial class TickRecorderAvaloniaWindow : Window
    9: public TickRecorderAvaloniaWindow() => InitializeComponent();
```

## src/windows/Tools/TradingTerminal.Recording/RecordingServiceCollectionExtensions.cs
```cs
    6: public static class RecordingServiceCollectionExtensions
    8: public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.Recording/TickRecorderView.xaml.cs
```cs
    7: public partial class TickRecorderView : UserControl
    9: public TickRecorderView() { InitializeComponent(); }
```

## src/windows/Tools/TradingTerminal.Recording/TickRecorderViewModel.cs
```cs
   24: public sealed partial class TickRecorderViewModel : ViewModelBase, IDisposable
   33: public TickRecorderViewModel(
   52: public ObservableCollection<SignalInstrument> Instruments { get; }
   55: public IReadOnlyList<SignalInstrument> AllInstruments { get; }
   61: public ObservableCollection<RecorderTickPreview> RecentTicks { get; } = new();
   62: public const int PreviewSize = 30;
  181: public void Dispose()
  203: public sealed record RecorderTickPreview(DateTime TimestampUtc, double Bid, double Ask, long BidSize, long AskSize)
  205: public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
```
