# TradingTerminal.Strategies.ImbalanceHeatFront — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/DependencyInjection.cs
```cs
    7: public static class DependencyInjection
    9: public static IServiceCollection AddImbalanceHeatFrontStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/Engine/ImbalanceHeatFrontStrategyEngine.cs
```cs
   31: public sealed class ImbalanceHeatFrontStrategy : IBacktestStrategy
   33: public enum RidgeMode { Momentum, MeanReversion }
   35: public int NumLevels { get; }
   36: public int NumSlices { get; }
   37: public double RidgeThreshold { get; }
   38: public int RidgeWidth { get; }
   39: public int ConfirmationSlices { get; }
   40: public RidgeMode Mode { get; }
   41: public long Quantity { get; }
   42: public double StopLossPips { get; }
   43: public double TakeProfitPips { get; }
   58: public ImbalanceHeatFrontStrategy(
   94: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
   96: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  110: public async Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
  126: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
  128: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
```

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontCalculator.cs
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

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontPlugin.cs
```cs
    8: public sealed class ImbalanceHeatFrontPlugin : IStrategyPlugin
   10: public string Name => "Imbalance Heat Front";
   11: public string TargetSdkVersion => SdkInfo.Version;
   12: public void Register(IPluginRegistrar registrar) => registrar.Services.AddImbalanceHeatFrontStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontStrategy.cs
```cs
   11: public sealed class ImbalanceHeatFrontStrategy : ITradingStrategy
   13: public string Id => "imbalance.heatfront";
   14: public string DisplayName => "Imbalance Heat Front (L2 pressure surface)";
   15: public string Description => "3D bid/ask imbalance surface over [distance-from-touch × time]. Detects coherent ridges of one-sided book pressure and enters with (mo...
   22: public StrategyDataRequirement DataRequirement =>
```

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontViewModel.cs
```cs
   21: public sealed partial class ImbalanceHeatFrontViewModel : LiveSignalStrategyViewModelBase
   53: public double[,]? Surface { get; private set; }
   55: public event EventHandler? SurfaceChanged;
   68: protected override void OnPauseReleased()
   78: public ImbalanceHeatFrontViewModel(
  111: protected override IBacktestStrategy BuildStrategy(Contract contract)
  146: protected override string? ValidateSetup()
  160: protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
```

## src/windows/Strategies/TradingTerminal.Strategies.ImbalanceHeatFront/ImbalanceHeatFrontWindow.xaml.cs
```cs
    9: public partial class ImbalanceHeatFrontWindow : MetroWindow
   20: public ImbalanceHeatFrontWindow()
```
