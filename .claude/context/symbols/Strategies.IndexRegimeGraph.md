# TradingTerminal.Strategies.IndexRegimeGraph — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/AvaloniaUi/IndexRegimeGraphAvaloniaWindow.axaml.cs
```cs
   10: public partial class IndexRegimeGraphAvaloniaWindow : Window
   12: public IndexRegimeGraphAvaloniaWindow() => InitializeComponent();
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddIndexRegimeGraphStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphPlugin.cs
```cs
   14: public sealed class IndexRegimeGraphPlugin : IStrategyPlugin
   16: public string Name => "Index Regime Graph";
   18: public string TargetSdkVersion => SdkInfo.Version;
   20: public void Register(IPluginRegistrar registrar) => registrar.Services.AddIndexRegimeGraphStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphStrategy.cs
```cs
    6: public sealed class IndexRegimeGraphStrategy : ITradingStrategy
    8: public string Id => "index.regime.graph";
    9: public string DisplayName => "Index Regime Graph";
   10: public string Description =>
   18: public StrategyDataRequirement DataRequirement =>
   22: public IReadOnlyList<AssetClass> AssetClasses => new[] { AssetClass.Index, AssetClass.Equity };
   24: public StrategyAssetScope AssetScope => StrategyAssetScope.MultiAsset;
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphViewModel.cs
```cs
   31: public sealed partial class IndexRegimeGraphViewModel : ViewModelBase, IDisposable
   83: public IndexRegimeGraphViewModel(
  110: public ObservableCollection<IndexFamily> Families { get; }
  111: public ObservableCollection<RegimeHorizon> Horizons { get; }
  112: public ObservableCollection<BrokerKind> Brokers { get; }
  113: public ObservableCollection<string> TimeframeHeaders { get; }
  114: public ObservableCollection<ConstituentRow> ConstituentRows { get; }
  117: public ObservableCollection<string> SettingsTargets { get; }
  159: public string HorizonDescription => TimeframeWeighting.Describe(SelectedHorizon);
  164: public string CompositeColorHex => BandColors.Hex(CompositeBand);
  677: public ObservableCollection<string> PresetNames { get; } = new();
  754: public void Dispose()
  765: public sealed record RegimeGraphPreset(int RefreshSeconds, bool AutoRefresh);
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphWindow.xaml.cs
```cs
   11: public partial class IndexRegimeGraphWindow : MetroWindow
   15: public IndexRegimeGraphWindow()
```

## src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/RegimeTableModels.cs
```cs
    9: public enum AssetLoadState
   29: public static string Hex(CellSignal band) => band switch
   39: public static string Tint(CellSignal band) => band switch
   48: public static string DirectionText(CellSignal band) => band switch
   58: public static string Arrow(CellSignal band) => band switch
   71: public static string Short(AdvancedIndicatorRow row) => row switch
   97: public sealed record HeatCell(string Label, double Score, CellSignal Band, bool HasData)
   99: public string TintHex => HasData ? BandColors.Tint(Band) : "#0AFFFFFF";
  100: public string TextHex => HasData ? BandColors.Hex(Band) : "#5A6473";
  101: public string Text => !HasData ? "·" : $"{Score * 100:+0;-0;0}";
  105: public sealed record IndicatorCell(CellSignal Band, string ToolTip, bool HasData)
  107: public string Arrow => HasData ? BandColors.Arrow(Band) : "·";
  108: public string TintHex => HasData ? BandColors.Tint(Band) : "#0AFFFFFF";
  109: public string TextHex => HasData ? BandColors.Hex(Band) : "#5A6473";
  114: public sealed record IndicatorRow(string Label, IReadOnlyList<IndicatorCell> Cells);
  122: public sealed partial class ConstituentRow : ObservableObject
  124: public required string Symbol { get; init; }
  125: public required double IndexWeight { get; init; }
  126: public required double StockScore { get; init; }
  127: public required double Contribution { get; init; }
  128: public required CellSignal Band { get; init; }
  129: public required bool HasData { get; init; }
  130: public required IReadOnlyList<HeatCell> Cells { get; init; }
  131: public required IReadOnlyList<IndicatorRow> IndicatorMatrix { get; init; }
  148: public bool IsBusy => LoadState is AssetLoadState.Pending or AssetLoadState.Loading;
  151: public bool ShowLoadStatus => LoadState != AssetLoadState.Ready;
  154: public string LoadGlyph => LoadState switch
  162: public string LoadGlyphHex => LoadState switch
  170: public string LoadStatusText => LoadState switch
  179: public bool HasMatrix => IndicatorMatrix.Count > 0;
  181: public string ScoreHex => BandColors.Hex(Band);
  182: public string TintHex => BandColors.Tint(Band);
  183: public string DirectionText => BandColors.DirectionText(Band);
  184: public double RowOpacity => HasData ? 1.0 : 0.45;
  186: public string WeightText => IndexWeight.ToString("P1");
  187: public string ScoreText => HasData ? $"{StockScore * 100:+0.0;-0.0;0.0}" : "—";
  188: public string ContribText => HasData ? $"{Contribution * 100:+0.00;-0.00;0.00}" : "—";
  193: public static ConstituentRow From(ConstituentRegimeScore c, IReadOnlyList<string> tfLabels)
```
