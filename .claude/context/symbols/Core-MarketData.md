# TradingTerminal.Core / MarketData — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeBarIndicators.cs
```cs
   14: public static class AdvancedRegimeBarIndicators
   18: public static double SmaTail(IReadOnlyList<double> values, int length)
   33: public static double[] Closes(IReadOnlyList<Bar> bars)
   46: public static double[] TrueRangeAtr(IReadOnlyList<Bar> bars, int length)
   84: public static (double MacdLine, double SignalLine, double Histogram) Macd(
  119: public static double[] Ema(IReadOnlyList<double> values, int period)
  142: public static double Cci(IReadOnlyList<Bar> bars, int length)
  173: public static (double Line, bool IsBullish) SuperTrend(IReadOnlyList<Bar> bars, double factor, int atrLength)
  249: public static double SessionVwap(IReadOnlyList<Bar> bars)
  271: public static double PocApprox(IReadOnlyList<Bar> bars, int lookback)
  292: public static double RangePosition(IReadOnlyList<Bar> bars, int length)
  308: public static double BarDelta(Bar bar)
```

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeCalculator.cs
```cs
   12: public static class AdvancedRegimeCalculator
   16: public static AdvancedRegimeSnapshot Compute(
```

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeModels.cs
```cs
   11: public sealed record AdvancedTimeframe(string Label, TimeSpan Bucket, bool Enabled)
   14: public static IReadOnlyList<AdvancedTimeframe> Defaults { get; } = new[]
   31: public enum AdvancedIndicatorRow
   54: public enum CellSignal
   69: public sealed record AdvancedRegimeCell(
   80: public sealed record AdvancedRegimeColumn(
   90: public sealed record AdvancedRegimeSnapshot(
   96: public static AdvancedRegimeSnapshot Empty { get; } = new(
```

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeSettings.cs
```cs
    8: public sealed class AdvancedRegimeSettings
   11: public int RsiLength { get; set; } = 14;
   12: public double RsiOverbought { get; set; } = 70;
   13: public double RsiOversold { get; set; } = 30;
   16: public int MacdFast { get; set; } = 12;
   17: public int MacdSlow { get; set; } = 26;
   18: public int MacdSignal { get; set; } = 9;
   21: public int CciLength { get; set; } = 20;
   24: public int Ma9Length { get; set; } = 9;
   25: public int Ma21Length { get; set; } = 21;
   26: public int Ma50Length { get; set; } = 50;
   29: public int TripleMaFast { get; set; } = 8;
   30: public int TripleMaMid { get; set; } = 21;
   31: public int TripleMaSlow { get; set; } = 50;
   34: public int StdLength { get; set; } = 20;
   37: public double SuperTrendFactor { get; set; } = 3.0;
   38: public int SuperTrendAtrLength { get; set; } = 10;
   41: public int AtrLength { get; set; } = 14;
   42: public int AtrRegressionLength { get; set; } = 50;
   45: public int PocLookback { get; set; } = 50;
   46: public int TrendRangeLength { get; set; } = 20;
   49: public int DeltaLookback { get; set; } = 20;
   52: public bool EnableRsi { get; set; } = true;
   53: public bool EnableMacd { get; set; } = true;
   54: public bool EnableCci { get; set; } = true;
   55: public bool EnableMa9 { get; set; } = true;
   56: public bool EnableMa21 { get; set; } = true;
   57: public bool EnableMa50 { get; set; } = true;
   58: public bool EnableTripleMa { get; set; } = true;
   59: public bool EnableVwap { get; set; } = true;
   60: public bool EnableSuperTrend { get; set; } = true;
   61: public bool EnableAtr { get; set; } = true;
   62: public bool EnableAtrRegression { get; set; } = true;
   63: public bool EnableStd { get; set; } = true;
   64: public bool EnablePocPosition { get; set; } = true;
   65: public bool EnableTrendRange { get; set; } = true;
   66: public bool EnableDelta { get; set; } = true;
   67: public bool EnableCumulativeDelta { get; set; } = true;
   68: public bool EnableVolumeBuySell { get; set; } = true;
   69: public bool EnableTrend { get; set; } = true;
   72: public bool ShowValue { get; set; } = true;
   73: public bool ShowDirection { get; set; } = true;
   76: public bool IsRowEnabled(AdvancedIndicatorRow row) => row switch
  100: public static AdvancedRegimeSettings Default => new();
  105: public AdvancedRegimeSettings Clone() => (AdvancedRegimeSettings)MemberwiseClone();
```

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/BarTimeframeAggregator.cs
```cs
   10: public static class BarTimeframeAggregator
   17: public static IReadOnlyList<Bar> Aggregate(IReadOnlyList<Bar> baseBars, TimeSpan bucket)
```

## src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/IAdvancedRegimeProvider.cs
```cs
   15: public interface IAdvancedRegimeProvider
   17:     Task<AdvancedRegimeSnapshot> AnalyseAsync(
   18:     Contract contract,
   19:     BrokerKind broker,
   20:     string displaySymbol,
   21:     IReadOnlyList<AdvancedTimeframe> timeframes,
   22:     AdvancedRegimeSettings settings,
   23:     CancellationToken cancellationToken = default);
```

## src/windows/Core/TradingTerminal.Core/MarketData/Archive/ArchiveModels.cs
```cs
    4: public enum ArchivePeriod
   12: public enum ArchiveTables
   25: public sealed record ArchiveTarget(string Kind, string? ChatRef)
   27: public static ArchiveTarget SavedMessages { get; } = new("saved", null);
   28: public static ArchiveTarget Chat(string chatRef) => new("chat", chatRef);
   29: public bool IsSavedMessages => Kind == "saved";
   35: public sealed record ArchiveBlobRef(
   44: public sealed record ArchiveManifestEntry(
   62: public sealed record ArchiveResult(
   69: public sealed record ArchiveCoverageWindow(
   77: public sealed record InstantOffloadResult(
   85: public static class ArchivePeriodMath
   87: public static (DateTime FromUtc, DateTime ToUtc) ClosedPeriod(DateTime nowUtc, ArchivePeriod period) =>
  111: public static DateTime PeriodStart(DateTime utc, ArchivePeriod period)
  123: public static (DateTime FromUtc, DateTime ToUtc) PeriodWindow(DateTime startUtc, ArchivePeriod period) =>
  135: public static IEnumerable<(DateTime FromUtc, DateTime ToUtc)> ClosedWindows(
```

## src/windows/Core/TradingTerminal.Core/MarketData/Archive/IArchiveTransport.cs
```cs
    9: public interface IArchiveTransport
   13:     string Name { get; }
   16:     bool IsReady { get; }
   21:     Task<ArchiveBlobRef> UploadAsync(
   22:     Stream content,
   23:     string displayName,
   24:     long contentLength,
   25:     ArchiveTarget target,
   26:     IProgress<long>? progress,
   27:     CancellationToken ct);
   30:     Task DownloadAsync(
   31:     ArchiveBlobRef blob,
   32:     Stream destination,
   33:     IProgress<long>? progress,
   34:     CancellationToken ct);
```

## src/windows/Core/TradingTerminal.Core/MarketData/Archive/IMarketDataArchiver.cs
```cs
    8: public interface IMarketDataArchiver
   13:     Task<ArchiveResult> ArchiveRangeAsync(
   14:     DateTime fromUtc, DateTime toUtc,
   15:     ArchiveTarget target,
   16:     IProgress<string>? progress,
   17:     CancellationToken ct);
   20:     Task<IReadOnlyList<ArchiveManifestEntry>> ListArchivesAsync(
   21:     string? transport = null, int maxRows = 200, CancellationToken ct = default);
   26:     Task<IReadOnlyList<ArchiveCoverageWindow>> GetCoverageAsync(CancellationToken ct = default);
   31:     Task<InstantOffloadResult> OffloadPendingAsync(IProgress<string>? progress, CancellationToken ct);
   36:     Task RestoreAsync(
   37:     ArchiveManifestEntry entry,
   38:     IProgress<string>? progress,
   39:     CancellationToken ct);
```

## src/windows/Core/TradingTerminal.Core/MarketData/Archive/ITelegramArchiveLogin.cs
```cs
   10: public sealed record TelegramArchiveCredentials(int ApiId, string ApiHash, string PhoneNumber);
   15: public sealed record TelegramArchiveLoginResult(bool Success, string Message);
   24: public interface ITelegramArchiveLogin
   27:     bool IsConnected { get; }
   30:     TelegramArchiveCredentials Load();
   34:     Task<TelegramArchiveLoginResult> ConnectAsync(
   35:     TelegramArchiveCredentials credentials, CancellationToken cancellationToken = default);
```

## src/windows/Core/TradingTerminal.Core/MarketData/CuratedInstrumentCatalog.cs
```cs
   14: public static class CuratedInstrumentCatalog
   22: public static IReadOnlyList<TradableInstrument> ForInteractiveBrokers { get; } = BuildFor(BrokerKind.InteractiveBrokers, includeEquities: true);
   25: public static IReadOnlyList<TradableInstrument> Futures { get; } = BuildFor(BrokerKind.NinjaTrader, includeEquities: false);
   30: public static IReadOnlyList<TradableInstrument> BuildFor(BrokerKind broker, bool includeEquities)
```

## src/windows/Core/TradingTerminal.Core/MarketData/FootprintFeatures.cs
```cs
   13: public enum FeedQuality
   26: public static class FeedQualityExtensions
   29: public static double Multiplier(this FeedQuality q) => q switch
   42: public readonly record struct FootprintPrint(double Price, long Size, AggressorSide Aggressor, DateTime TimeUtc)
   45: public static FootprintPrint From(TradePrint t) => new(t.Price, t.Size, t.Aggressor, t.EventTimeUtc);
   65: public sealed record FootprintFeatureRow(
   75: public long TotalVolume => BuyVolume + SellVolume;
   78: public long Delta => BuyVolume - SellVolume;
  108: public sealed record FootprintBar(
  125: public long TotalVolume => BuyVolume + SellVolume;
  130: public readonly record struct FootprintExtractorOptions(double ImbalanceRatio = 3.0)
  135: public static FootprintExtractorOptions Default => new(ImbalanceRatio: 3.0);
  155: public static class FootprintFeatures
  170: public static FootprintBar BuildBar(
  289: public static double SuggestRowSize(double barAtr, double instrumentTickSize, int targetRows = 20)
  313: public static IEnumerable<FootprintPrint> SyntheticPrints(
  331: public static readonly DescendingComparer Instance = new();
  332: public int Compare(double x, double y) => y.CompareTo(x);
```

## src/windows/Core/TradingTerminal.Core/MarketData/FootprintTimeBucketer.cs
```cs
   18: public sealed class FootprintTimeBucketer
   27: public FootprintTimeBucketer(TimeSpan span, double tickSize, FeedQuality quality)
   38: public DateTime CurrentBucketStart => _bucketStart;
   42: public long CumulativeDelta => _cumulativeDelta;
   46: public FootprintBar? Add(FootprintPrint print)
   70: public FootprintBar? BuildForming() =>
   76: public void Reset(long cumulativeDeltaSeed = 0)
```

## src/windows/Core/TradingTerminal.Core/MarketData/IBrokerClient.cs
```cs
   16: public interface IBrokerClient : IAsyncDisposable
   18:     BrokerKind Kind { get; }
   20:     IObservable<ConnectionState> ConnectionState { get; }
   22:     Task ConnectAsync(CancellationToken ct = default);
   31:     Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default);
   33:     Task DisconnectAsync(CancellationToken ct = default);
   35:     Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   36:     Contract contract,
   37:     BarSize barSize,
   38:     TimeSpan duration,
   39:     CancellationToken ct = default);
   41:     IAsyncEnumerable<Bar> SubscribeBarsAsync(
   42:     Contract contract,
   43:     BarSize barSize,
   44:     CancellationToken ct = default);
   51:     IAsyncEnumerable<Tick> SubscribeTicksAsync(
   52:     Contract contract,
   53:     CancellationToken ct = default);
   67:     IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
   68:     Contract contract,
   69:     int levels = 10,
   70:     CancellationToken ct = default);
   83:     IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
   84:     Contract contract,
   85:     CancellationToken ct = default);
   97:     Task<IReadOnlyList<TradeTick>> RequestHistoricalTradesAsync(
   98:     Contract contract,
   99:     DateTime fromUtc,
  100:     DateTime toUtc,
  101:     int maxTrades,
  102:     CancellationToken ct = default) =>
  103:     throw new NotSupportedException($"{Kind} does not provide historical trades.");
```

## src/windows/Core/TradingTerminal.Core/MarketData/IInstrumentRegistry.cs
```cs
   14: public interface IInstrumentRegistry
   17:     Instrument? Get(InstrumentId id);
   20:     InstrumentId? Resolve(BrokerKind broker, string brokerSymbol);
   27:     InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker);
   31:     string? ToBrokerSymbol(InstrumentId id, BrokerKind broker);
   34:     void RegisterAlias(InstrumentAlias alias);
   37:     IReadOnlyList<Instrument> All();
```

## src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataHub.cs
```cs
   15: public interface IMarketDataHub
   17:     IObservable<Quote> Quotes(InstrumentId instrumentId);
   18:     IObservable<TradePrint> Trades(InstrumentId instrumentId);
   19:     IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size);
   20:     IObservable<DepthSnapshot> Depth(InstrumentId instrumentId);
   23:     void PublishQuote(Quote quote);
   24:     void PublishTrade(TradePrint trade);
   25:     void PublishBar(OhlcvBar bar);
   26:     void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot);
```

## src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataIngest.cs
```cs
   14: public interface IMarketDataIngest
   17:     InstrumentId Resolve(Contract contract, BrokerKind broker);
   21:     IDisposable Subscribe(Contract contract, BrokerKind broker);
   24:     IDisposable SubscribeBars(Contract contract, BrokerKind broker, BarSize size);
   31:     IDisposable SubscribeTrades(Contract contract, BrokerKind broker);
```

## src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataRepository.cs
```cs
   17: public interface IMarketDataRepository
   24:     Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default);
   26:     Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
   27:     Contract contract,
   28:     BrokerKind broker,
   29:     BarSize barSize,
   30:     TimeSpan duration,
   31:     CancellationToken ct = default);
   38:     IAsyncEnumerable<Bar> SubscribeBarsAsync(
   39:     Contract contract,
   40:     BrokerKind broker,
   41:     BarSize barSize,
   42:     CancellationToken ct = default);
   49:     IAsyncEnumerable<Tick> SubscribeTicksAsync(
   50:     Contract contract,
   51:     BrokerKind broker,
   52:     CancellationToken ct = default);
   60:     IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
   61:     Contract contract,
   62:     BrokerKind broker,
   63:     int levels = 10,
   64:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataStore.cs
```cs
   12: public interface IMarketDataStore
   15:     void EnqueueQuote(Quote quote);
   18:     void EnqueueTrade(TradePrint trade);
   22:     void EnqueueBar(OhlcvBar bar);
   27:     void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source);
   30:     Task FlushAsync(CancellationToken ct = default);
   36:     Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) =>
   37:     Task.FromResult(StoredDataExtent.Empty);
   43:     Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
   44:     InstrumentId instrumentId, BarSize size, int count, BrokerKind? source = null,
   45:     CancellationToken ct = default);
   49:     IAsyncEnumerable<Quote> ReadQuotesAsync(
   50:     InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
   51:     CancellationToken ct = default);
   55:     IAsyncEnumerable<TradePrint> ReadTradesAsync(
   56:     InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null,
   57:     CancellationToken ct = default);
   61:     IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
   62:     InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
   67:     IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
   68:     InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
   69:     BrokerKind? source = null, CancellationToken ct = default);
   73:     Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
   76:     Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
   79:     Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
   84:     Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/MarketData/IQuestDbLauncher.cs
```cs
   10: public interface IQuestDbLauncher
   13:     bool IsApplicable { get; }
   16:     bool AutoStart { get; }
   19:     bool IsReachable();
   23:     Task<bool> StartAsync(CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/MarketData/Indicators.cs
```cs
   13: public static class Indicators
   15: public sealed class SimpleMovingAverage
   19: public int Period { get; }
   20: public SimpleMovingAverage(int period) { Period = period; _buf = new Queue<double>(period); }
   21: public bool IsReady => _buf.Count == Period;
   22: public double Value => _buf.Count == 0 ? 0 : _sum / _buf.Count;
   23: public void Push(double v)
   30: public sealed class RollingStdev
   35: public int Period { get; }
   36: public RollingStdev(int period) { Period = period; _buf = new Queue<double>(period); }
   37: public bool IsReady => _buf.Count == Period;
   38: public double Mean => _buf.Count == 0 ? 0 : _sum / _buf.Count;
   39: public double Value
   50: public void Push(double v)
   62: public sealed class ExponentialMovingAverage
   64: public int Period { get; }
   65: public double Alpha { get; }
   66: public double Value { get; private set; }
   67: public bool IsReady => _count >= Period;
   69: public ExponentialMovingAverage(int period)
   74: public void Push(double v)
   86: public sealed class RelativeStrengthIndex
   88: public int Period { get; }
   93: public RelativeStrengthIndex(int period) { Period = period; }
   94: public bool IsReady => _samples > Period;
   95: public double Value
  104: public void Push(double v)
  129: public sealed class AverageTrueRange
  134: public int Period => _sma.Period;
  135: public bool IsReady => _sma.IsReady;
  136: public double Value => _sma.Value;
  137: public AverageTrueRange(int period) { _sma = new SimpleMovingAverage(period); }
  138: public void Push(double v)
```

## src/windows/Core/TradingTerminal.Core/MarketData/InstrumentDataView.cs
```cs
   30: public sealed class InstrumentDataView
   35: public InstrumentId InstrumentId { get; }
   45: public IAsyncEnumerable<Quote> Quotes(DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
   50: public IAsyncEnumerable<TradePrint> Trades(DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
   55: public IAsyncEnumerable<OhlcvBar> Bars(BarSize size, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, CancellationToken ct = default) =>
   60: public Task<IReadOnlyList<OhlcvBar>> RecentBars(BarSize size, int count, BrokerKind? source = null, CancellationToken ct = default) =>
   66: public static class MarketDataStoreInstrumentExtensions
   69: public static InstrumentDataView Instrument(this IMarketDataStore store, InstrumentId instrumentId) =>
```

## src/windows/Core/TradingTerminal.Core/MarketData/Microstructure.cs
```cs
   10: public static class Microstructure
   18: public static double Microprice(double bid, double ask, long bidSize, long askSize)
   26: public static double Microprice(Tick t) => Microprice(t.Bid, t.Ask, t.BidSize, t.AskSize);
   32: public static double QueueImbalance(long bidSize, long askSize)
   40: public static double QueueImbalance(Tick t) => QueueImbalance(t.BidSize, t.AskSize);
   43: public static double HalfSpread(double bid, double ask) => (ask - bid) * 0.5;
   46: public static double HalfSpread(Tick t) => HalfSpread(t.Bid, t.Ask);
   60: public static double CumulativeImbalance(DepthSnapshot snapshot, int depthLevels = 5)
   75: public static double WeightedMidPrice(DepthSnapshot snapshot, int depthLevels = 5)
   98: public static long SideDepth(IReadOnlyList<DepthLevel> side, int depthLevels = 5)
  113: public static double EstimatedSlippage(
  141: public static double LargestLevelGap(IReadOnlyList<DepthLevel> side)
  167: public static AggressorSide ClassifyAggressor(
```

## src/windows/Core/TradingTerminal.Core/MarketData/OrderFlowImbalance.cs
```cs
   18: public static class OrderFlowImbalance
   22: public const int RegimeCount = 9;
   29: public static double TradeImbalance(long buyCount, long sellCount)
   42: public static int Regime(double obi)
   59: public static bool IsStrong(double obi, int strongRegime) =>
   63: public static string RegimeLabel(int regime) =>
```

## src/windows/Core/TradingTerminal.Core/MarketData/Sp100Sp500Catalog.cs
```cs
    6: public readonly record struct SpSymbol(string Symbol, string Name);
   21: public static class Sp100Sp500Catalog
   24: public static IReadOnlyList<SpSymbol> Sp100 { get; } = BuildSp100();
   28: public static IReadOnlyList<SpSymbol> Sp500 { get; } = BuildSp500();
   31: public static IReadOnlyList<Contract> Sp100Contracts { get; } = Map(Sp100);
   34: public static IReadOnlyList<Contract> Sp500Contracts { get; } = Map(Sp500);
   38: public static Contract ToContract(SpSymbol s) => Contract.UsStock(s.Symbol);
```

## src/windows/Core/TradingTerminal.Core/MarketData/StoredDataExtent.cs
```cs
    8: public sealed record StoredDataExtent(DateTime? EarliestUtc, DateTime? LatestUtc)
   10: public static StoredDataExtent Empty { get; } = new(null, null);
   13: public bool HasData => EarliestUtc is not null && LatestUtc is not null;
   16: public static StoredDataExtent Combine(StoredDataExtent a, StoredDataExtent b) =>
```

## src/windows/Core/TradingTerminal.Core/MarketData/TradableInstrument.cs
```cs
   18: public sealed record TradableInstrument(
```

## src/windows/Core/TradingTerminal.Core/MarketData/VolumeTimeBucketer.cs
```cs
   16: public sealed record VolumeBucket(
   24: public long TotalVolume => BuyVolume + SellVolume;
   27: public double BuyFraction => TotalVolume > 0 ? (double)BuyVolume / TotalVolume : 0.5;
   30: public long Delta => BuyVolume - SellVolume;
   40: public static class VolumeTimeBucketer
   48: public static IEnumerable<VolumeBucket> Bucketize(
   98: public static long AdaptiveBucketVolume(IReadOnlyList<long> barVolumes)
  109: public static long VpinBucketVolume(long dailyVolume)
```
