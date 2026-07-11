# TradingTerminal.Strategies.SigmaIcFlow — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/AvaloniaUi/SigmaIcFlowStrategyAvaloniaWindow.axaml.cs
```cs
   13: public partial class SigmaIcFlowStrategyAvaloniaWindow : Window
   15: public SigmaIcFlowStrategyAvaloniaWindow() => InitializeComponent();
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/DependencyInjection.cs
```cs
    8: public static class DependencyInjection
   10: public static IServiceCollection AddSigmaIcFlowStrategy(this IServiceCollection services)
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexLineFit.cs
```cs
   15: public sealed record ApexLineFit(
   24: public static ApexLineFit Empty => new(0, 0, 0, 0, 0, 0);
   27: public double SlopeTStat => NeweyWestStandardError > 1e-300 ? Slope / NeweyWestStandardError : 0.0;
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexScalperStrategy.cs
```cs
   14: public sealed record ApexLiveCandle(
   69: public sealed class ApexScalperStrategy : IBacktestStrategy
   72: public const string SigDelta = "DELTA";
   73: public const string SigVpin = "VPIN";
   74: public const string SigFootprint = "FOOTPRINT";
   75: public const string SigTapeSpeed = "TAPE_SPEED";
   76: public const string SigKyle = "KYLE";
   77: public const string SigInitiative = "INITIATIVE";
   78: public const string SigControl = "CONTROL";
   79: public const string SigWedge = "WEDGE";
   80: public const string SigValue = "VALUE";
   81: public const string SigCvd = "CVD";
   82: public const string SigObi = "OBI";
   83: public const string SigPredNode = "PRED_NODE";
   88: public static readonly string[] SignalNames =
   95: public ApexV2Options Options { get; }
   96: public TimeSpan CandleInterval { get; }
   99: public double InstrumentTick { get; }
  210: public bool PaperTradingEnabled { get; set; } = true;
  217: public IReadOnlyList<ApexTradeRecord> Trades => _trades;
  220: public double OpenEntryPrice => _entryPrice;
  221: public double OpenStopPrice => _stopPrice;
  222: public double OpenTargetPrice => _targetPrice;
  223: public double Balance => _balance;
  230: public ApexSnapshotV2? Latest { get; private set; }
  233: public IReadOnlyList<ApexSnapshotV2> History
  246: public IReadOnlyList<FootprintBar> FootprintBars
  262: public ApexLiveCandle? LiveCandle
  277: public ApexScalperStrategy(Contract contract, ApexV2Options? options = null, TimeSpan? candleInterval = null, double instrumentTick = 0.25)
  312: public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
  319: public void SeedFromBars(IReadOnlyList<Bar> bars)
  377: public Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
  391: public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
  404: public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
  586: public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
  592: public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
 1762: public static SignalResult Invalid(string name, DateTime now) => new(name, 0, 0, 0, false, now);
 1763: public static SignalResult From(string name, double score, double confidence, DateTime now)
 1786: public static LineTriple Empty => new(ApexLineFit.Empty, ApexLineFit.Empty, ApexLineFit.Empty, 0);
 1787: public bool Valid => Buy.FittedEndpoint > 0 && Sell.FittedEndpoint > 0;
 1792: public DateTime OpenTime;
 1793: public double Open, High, Low, Close;
 1794: public long BuyVolume, SellVolume;
 1802: public void Add(FootprintPrint p)
 1808: public double ArrivalRate(DateTime now, double windowSec)
 1817: public double UpTickFraction(DateTime now, double windowSec)
 1840: public RingBuffer(int capacity) { _capacity = Math.Max(1, capacity); _q = new Queue<T>(_capacity); }
 1841: public int Count => _q.Count;
 1842: public int Capacity => _capacity;
 1843: public void Push(T v) { _q.Enqueue(v); while (_q.Count > _capacity) _q.Dequeue(); }
 1844: public T[] ToArray() => _q.ToArray();
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexSnapshotV2.cs
```cs
   17: public sealed record ApexSignalState(
   27: public double TtlRemaining => TtlMs > 1e-9 ? Math.Clamp(1.0 - AgeMs / TtlMs, 0.0, 1.0) : 0.0;
   73: public sealed record ApexSnapshotV2(
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexTradeRecord.cs
```cs
   17: public sealed record ApexTradeRecord(
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexV2Options.cs
```cs
   15: public enum ApexBarMode
   31: public readonly record struct ApexTtlMultipliers(
   40: public static ApexTtlMultipliers Default => new(DeltaFootprint: 1.5, ObiTapeSpeed: 0.5, PocLines: 3.0);
   58: public sealed record ApexV2Options
   66: public double ReferenceSpanSeconds { get; init; } = 60.0;
   70: public ApexBarMode BarMode { get; init; } = ApexBarMode.Time;
   76: public long VolumeBarSize { get; init; } = 2_000;
   80: public ApexTtlMultipliers TtlMultipliers { get; init; } = ApexTtlMultipliers.Default;
   84: public double EwDelta { get; init; } = 0.9;
   90: public int? NeweyWestLag { get; init; }
   96: public int CovarianceWindow { get; init; } = 1_500;
  103: public int WeightRecalcEveryBars { get; init; } = 50;
  106: public int ForwardReturnHorizon { get; init; } = 5;
  109: public int KyleWindow { get; init; } = 50;
  116: public int IsotonicMinSamples { get; init; } = 500;
  122: public int BootstrapSampleThreshold { get; init; } = 500;
  129: public double KellyFraction { get; init; } = 0.25;
  132: public double KellyFractionOverlap { get; init; } = 0.25;
  135: public double KellyFractionAsian { get; init; } = 0.10;
  138: public double RiskFraction { get; init; } = 0.005;
  142: public int RowsPerBarTarget { get; init; } = 20;
  148: public double? TickSizeOverride { get; init; }
  151: public double ImbalanceRatio { get; init; } = 3.0;
  155: public int VpinLookbackBuckets { get; init; } = 50;
  159: public double StopSigmaCoefficient { get; init; } = 1.5;
  162: public double TargetSigmaCoefficient { get; init; } = 2.25;
  168: public double ValueAreaSigmaCoefficient { get; init; } = 1.0;
  172: public double AbsorptionVolumeFraction { get; init; } = 0.5;
  176: public double CompositeThreshold { get; init; } = 1.0;
  179: public IReadOnlyDictionary<string, double> RegimeMultipliers { get; init; } =
  191: public bool TradeAsian { get; init; }
  194: public bool TradeLondon { get; init; } = true;
  197: public bool TradeNewYork { get; init; } = true;
  200: public bool TradeLondonNy { get; init; } = true;
  204: public int CooldownSeconds { get; init; } = 30;
  207: public double MaxDailyLossFraction { get; init; } = 0.02;
  210: public double MaxDrawdownFraction { get; init; } = 0.05;
  214: public double CommissionPerSide { get; init; }
  217: public double SpreadCostTicks { get; init; } = 1.0;
  223: public double SlippageCoefficient { get; init; } = 1.0;
  227: public int PredictedNodeHorizon { get; init; } = 5;
  234: public double PredictionExitMinConfidence { get; init; } = 0.3;
  237: public double KalmanProcessNoise { get; init; } = 1e-4;
  240: public double KalmanMeasurementNoise { get; init; } = 1e-2;
  244: public double HawkesBaselineMu { get; init; }
  247: public double HawkesAlpha { get; init; } = 0.3;
  250: public double HawkesBeta { get; init; } = 0.5;
  253: public static ApexV2Options Default => new();
  263: public static ApexV2Options Backtest => Default with
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowPlugin.cs
```cs
   13: public sealed class SigmaIcFlowPlugin : IStrategyPlugin
   15: public string Name => "Σ⁻¹·IC Order-Flow Optimizer";
   17: public string TargetSdkVersion => SdkInfo.Version;
   19: public void Register(IPluginRegistrar registrar) => registrar.Services.AddSigmaIcFlowStrategy();
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategy.cs
```cs
    5: public sealed class SigmaIcFlowStrategy : ITradingStrategy
    7: public string Id => "sigma.ic.flow";
   11: public string? BacktestStrategyId => "sigmaIcFlow";
   13: public string DisplayName => "Σ⁻¹·IC Order-Flow Optimizer (tape-primary composite)";
   14: public string Description =>
   24: public StrategyDataRequirement DataRequirement =>
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyViewModel.cs
```cs
   20: public sealed record WeightRow(string SignalName, double Weight);
   32: public sealed partial class SigmaIcFlowStrategyViewModel : LiveSignalStrategyViewModelBase
   43: public sealed record CandleIntervalOption(string Label, TimeSpan Span)
   45: public override string ToString() => Label;
   48: public ObservableCollection<CandleIntervalOption> CandleIntervals { get; } = new(new[]
  155: public ObservableCollection<WeightRow> SignalWeights { get; } = new();
  160: public ObservableCollection<ApexSignalState> SignalStates { get; } = new();
  173: public sealed record PaperTradeRow(
  178: public ObservableCollection<PaperTradeRow> PaperTrades { get; } = new();
  197: public Engine.ApexScalperStrategy? EngineStrategy => _engine;
  201: public SigmaIcFlowStrategyViewModel(
  227: protected override StrategyDataRequirement DataRequirement =>
  233: protected override IBacktestStrategy BuildStrategy(Contract contract)
  259: protected override int WarmupBarCount => Math.Max(150, MaxChartCandles);
  261: protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
  269: protected override void OnBarsUpdated()
  448: protected override string? ValidateSetup()
```

## src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyWindow.xaml.cs
```cs
   15: public partial class SigmaIcFlowStrategyWindow : StrategyWindowBase
  136: public SigmaIcFlowStrategyWindow()
  153: protected override IEnumerable<WpfPlot> ChartHosts => new[] { SignalsPlot };
  155: protected override void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
  170: protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
  796: public Grid Root { get; }
  800: public LadderRow(Brush dimBrush)
  832: public void Set(double price, long size, double barWidth, Brush brush)
  840: public void Clear()
```
