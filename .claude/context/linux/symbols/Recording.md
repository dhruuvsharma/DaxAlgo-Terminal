# TradingTerminal.Recording — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.Recording/AvaloniaUi/TickRecorderAvaloniaWindow.axaml.cs
```cs
    7: public partial class TickRecorderAvaloniaWindow : Window
    9: public TickRecorderAvaloniaWindow() => InitializeComponent();
```

## src/linux/Tools/TradingTerminal.Recording/RecordingServiceCollectionExtensions.cs
```cs
    6: public static class RecordingServiceCollectionExtensions
    8: public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.Recording/TickRecorderView.xaml.cs
```cs
    5: public partial class TickRecorderView : UserControl
    7: public TickRecorderView() { InitializeComponent(); }
```

## src/linux/Tools/TradingTerminal.Recording/TickRecorderViewModel.cs
```cs
   24: public sealed partial class TickRecorderViewModel : ViewModelBase, IDisposable
   33: public TickRecorderViewModel(
   49: public IReadOnlyList<SignalInstrument> Instruments { get; }
   50: public ObservableCollection<RecorderTickPreview> RecentTicks { get; } = new();
   51: public const int PreviewSize = 30;
  161: public void Dispose()
  169: public sealed record RecorderTickPreview(DateTime TimestampUtc, double Bid, double Ask, long BidSize, long AskSize)
  171: public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
```
