# TradingTerminal.AdvancedMarketRegime — public API surface

Generated 2026-07-13. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeServiceCollectionExtensions.cs
```cs
   10: public static class AdvancedMarketRegimeServiceCollectionExtensions
   12: public static IServiceCollection AddAdvancedMarketRegimeSurface(this IServiceCollection services)
```

## src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeView.xaml.cs
```cs
    7: public partial class AdvancedMarketRegimeView : UserControl
    9: public AdvancedMarketRegimeView()
```

## src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeViewModel.cs
```cs
   26: public sealed partial class AdvancedMarketRegimeViewModel : ViewModelBase, IDisposable
   36: public const int MaxInstrumentsDisplayed = 500;
   60: public AdvancedMarketRegimeViewModel(
   96: public AdvancedRegimeSettings Settings { get; } = AdvancedRegimeSettings.Default;
   98: public ObservableCollection<TimeframeColumnOption> ColumnOptions { get; }
   99: public ObservableCollection<RowToggleOption> RowOptions { get; }
  100: public ObservableCollection<SignalInstrument> Instruments { get; private set; }
  101: public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }
  102: public ObservableCollection<string> HeaderCells { get; }
  103: public ObservableCollection<DashboardRow> Rows { get; }
  192: public ObservableCollection<string> PresetNames { get; }
  287: public async Task AnalyzeAsync()
  397: public void Dispose()
  429: public sealed record DashboardCell(string Glyph, string ValueText, string ColorHex, bool GlyphVisible, bool ValueVisible);
  432: public sealed record DashboardRow(string Label, IReadOnlyList<DashboardCell> Cells);
  436: public sealed partial class TimeframeColumnOption : ObservableObject
  438: public TimeframeColumnOption(string label, TimeSpan bucket, bool isEnabled)
  445: public string Label { get; }
  446: public TimeSpan Bucket { get; }
  452: public sealed partial class RowToggleOption : ObservableObject
  454: public RowToggleOption(AdvancedIndicatorRow row, string label)
  461: public AdvancedIndicatorRow Row { get; }
  462: public string Label { get; }
  470: public sealed record AdvancedRegimePreset(
```

## src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AvaloniaUi/AdvancedMarketRegimeAvaloniaWindow.axaml.cs
```cs
    8: public partial class AdvancedMarketRegimeAvaloniaWindow : Window
   10: public AdvancedMarketRegimeAvaloniaWindow() => InitializeComponent();
```
