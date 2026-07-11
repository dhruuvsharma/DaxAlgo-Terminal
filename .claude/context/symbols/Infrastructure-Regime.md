# TradingTerminal.Infrastructure / Regime — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/AaiiSentimentClient.cs
```cs
   15: public const string HttpClientName = "market-regime-aaii";
   21: public AaiiSentimentClient(IHttpClientFactory httpFactory, ILogger<AaiiSentimentClient> logger)
   27: public sealed record Sentiment(double Bull, double Bear);
   29: public async Task<Sentiment?> GetAsync(CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/AdvancedRegime/AdvancedRegimeService.cs
```cs
   18: public sealed class AdvancedRegimeService : IAdvancedRegimeProvider
   28: public AdvancedRegimeService(
   36: public async Task<AdvancedRegimeSnapshot> AnalyseAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/CnnFearGreedClient.cs
```cs
   14: public const string HttpClientName = "market-regime-cnn";
   20: public CnnFearGreedClient(IHttpClientFactory httpFactory, ILogger<CnnFearGreedClient> logger)
   27: public async Task<int?> GetAsync(CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/FredClient.cs
```cs
   17: public const string HttpClientName = "market-regime-fred";
   23: public FredClient(IHttpClientFactory httpFactory, ILogger<FredClient> logger)
   29: public async Task<double[]> GetSeriesAsync(string seriesId, string apiKey, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/Instrument/InstrumentRegimeService.cs
```cs
   16: public sealed class InstrumentRegimeService : IInstrumentRegimeProvider
   24: public InstrumentRegimeService(
   34: public async Task<InstrumentRegimeSnapshot> AnalyseAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/MarketRegimeService.cs
```cs
   42: public MarketRegimeService(
   58: public MarketRegimeSnapshot Current => _subject.Value;
   60: public IObservable<MarketRegimeSnapshot> Updates => _subject.AsObservable();
   62: public async Task<MarketRegimeSnapshot> RefreshAsync(CancellationToken ct = default)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/RegimeRefreshLoop.cs
```cs
   29: public RegimeRefreshLoop(
   41: public Task StartAsync(CancellationToken cancellationToken)
   54: public async Task StopAsync(CancellationToken cancellationToken)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/RegimeServiceCollectionExtensions.cs
```cs
   11: public static class RegimeServiceCollectionExtensions
   20: public static IServiceCollection AddMarketRegime(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/RegimeSignalGate.cs
```cs
   21: public RegimeSignalGate(IMarketRegimeProvider provider, IOptionsMonitor<MarketRegimeOptions> options)
   27: public bool ShouldSuppress(StrategyNotification notification, out string? reason)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Regime/YahooChartClient.cs
```cs
   14: public const string HttpClientName = "market-regime-yahoo";
   19: public YahooChartClient(IHttpClientFactory httpFactory, ILogger<YahooChartClient> logger)
   25: public sealed record Series(double? Price, double[] Closes)
   27: public static Series Empty { get; } = new(null, Array.Empty<double>());
   31: public async Task<Series> GetAsync(string symbol, CancellationToken ct)
```
