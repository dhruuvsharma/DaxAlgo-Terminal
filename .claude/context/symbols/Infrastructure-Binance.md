# TradingTerminal.Infrastructure / Binance — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Binance/RealBinanceClient.cs
```cs
   40: public RealBinanceClient(ILogger<RealBinanceClient> logger, IOptions<BinanceOptions> options)
   46: public BrokerKind Kind => BrokerKind.Binance;
   48: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   50: public async Task ConnectAsync(CancellationToken ct = default)
   79: public Task DisconnectAsync(CancellationToken ct = default)
   85: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  102: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  118: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
  125: public IAsyncEnumerable<Tick> SubscribeTicksAsync(
  132: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  139: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  153: public async Task<IReadOnlyList<TradeTick>> RequestHistoricalTradesAsync(
  200: public async ValueTask DisposeAsync()
```
