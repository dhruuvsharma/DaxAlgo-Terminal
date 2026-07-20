# TradingTerminal.Infrastructure / Kraken — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Kraken/RealKrakenClient.cs
```cs
   27: public RealKrakenClient(ILogger<RealKrakenClient> logger, IOptions<KrakenOptions> options)
   33: public BrokerKind Kind => BrokerKind.Kraken;
   34: public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);
   36: public async Task ConnectAsync(CancellationToken ct = default)
   56: public Task DisconnectAsync(CancellationToken ct = default)
   62: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   72: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   98: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
  102: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  106: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
  114: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  118: public async ValueTask DisposeAsync()
```
