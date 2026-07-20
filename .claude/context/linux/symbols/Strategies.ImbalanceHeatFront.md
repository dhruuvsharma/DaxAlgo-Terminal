# TradingTerminal.Strategies.ImbalanceHeatFront — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/DependencyInjection.cs
```cs
    6: public static class DependencyInjection
    8: public static IServiceCollection AddImbalanceHeatFrontStrategy(this IServiceCollection services)
```

## src/linux/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontCalculator.cs
```cs
   24: public sealed class ImbalanceHeatFrontCalculator
   26: public int NumLevels { get; }
   27: public int NumSlices { get; }
   28: public int EventsPerSlice { get; }
   29: public double RidgeThreshold { get; }
   30: public int RidgeWidth { get; }
   31: public int RidgeMemorySlices { get; }
   41: public long DepthEventsSeen { get; private set; }
   43: public ImbalanceHeatFrontCalculator(
   70: public void Reset()
   84: public readonly record struct RidgeState(int Side, double Height, int Trend, int StartLevel, int Width);
   86: public readonly record struct OnDepthResult(
   91: public OnDepthResult OnDepth(DepthSnapshot depth)
  136: public double[,] GetSurface()
```

## src/linux/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontStrategy.cs
```cs
   11: public sealed class ImbalanceHeatFrontStrategy : ITradingStrategy
   13: public string Id => "imbalance.heatfront";
   14: public string DisplayName => "Imbalance Heat Front (L2 pressure surface)";
   15: public string Description => "3D bid/ask imbalance surface over [distance-from-touch × time]. Detects coherent ridges of one-sided book pressure and enters with (mo...
   22: public StrategyDataRequirement DataRequirement =>
```

## src/linux/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontViewModel.cs
```cs
   21: public sealed partial class ImbalanceHeatFrontViewModel : LiveSignalStrategyViewModelBase
   53: public double[,]? Surface { get; private set; }
   55: public event EventHandler? SurfaceChanged;
   60: public ImbalanceHeatFrontViewModel(
   93: protected override IBacktestStrategy BuildStrategy(Contract contract)
  128: protected override string? ValidateSetup()
  142: protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
```

## src/linux/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontWindow.xaml.cs
```cs
    9: public partial class ImbalanceHeatFrontWindow : MetroWindow
   20: public ImbalanceHeatFrontWindow()
```
