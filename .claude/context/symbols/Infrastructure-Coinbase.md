# TradingTerminal.Infrastructure / Coinbase — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Coinbase/RealCoinbaseClient.cs
```cs
   26: public RealCoinbaseClient(ILogger<RealCoinbaseClient> logger, IOptions<CoinbaseOptions> options)
   34: public BrokerKind Kind => BrokerKind.Coinbase;
   35: public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);
   37: public async Task ConnectAsync(CancellationToken ct = default)
   57: public Task DisconnectAsync(CancellationToken ct = default)
   63: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   73: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   94: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
   97: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  100: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
  107: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  110: public async ValueTask DisposeAsync()
```
