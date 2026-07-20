# TradingTerminal.AdvancedMarketRegime — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeServiceCollectionExtensions.cs
```cs
   10: public static class AdvancedMarketRegimeServiceCollectionExtensions
   12: public static IServiceCollection AddAdvancedMarketRegimeSurface(this IServiceCollection services)
```

## src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml.cs
```cs
    5: public partial class AdvancedMarketRegimeView : UserControl
    7: public AdvancedMarketRegimeView()
```

## src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeViewModel.cs
```cs
   22: public sealed partial class AdvancedMarketRegimeViewModel : ViewModelBase, IDisposable
   32: public const int MaxInstrumentsDisplayed = 500;
   56: public AdvancedMarketRegimeViewModel(
   88: public AdvancedRegimeSettings Settings { get; } = AdvancedRegimeSettings.Default;
   90: public ObservableCollection<TimeframeColumnOption> ColumnOptions { get; }
   91: public ObservableCollection<RowToggleOption> RowOptions { get; }
   92: public ObservableCollection<SignalInstrument> Instruments { get; private set; }
   93: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
   94: public ObservableCollection<string> HeaderCells { get; }
   95: public ObservableCollection<DashboardRow> Rows { get; }
  184: public async Task AnalyzeAsync()
  294: public void Dispose()
  324: public sealed record DashboardCell(string Glyph, string ValueText, string ColorHex, bool GlyphVisible, bool ValueVisible);
  327: public sealed record DashboardRow(string Label, IReadOnlyList<DashboardCell> Cells);
  331: public sealed partial class TimeframeColumnOption : ObservableObject
  333: public TimeframeColumnOption(string label, TimeSpan bucket, bool isEnabled)
  340: public string Label { get; }
  341: public TimeSpan Bucket { get; }
  347: public sealed partial class RowToggleOption : ObservableObject
  349: public RowToggleOption(AdvancedIndicatorRow row, string label)
  356: public AdvancedIndicatorRow Row { get; }
  357: public string Label { get; }
```

## src/linux/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml.cs
```cs
    8: public partial class AdvancedMarketRegimeAvaloniaWindow : Window
   10: public AdvancedMarketRegimeAvaloniaWindow() => InitializeComponent();
```
