# TradingTerminal.Infrastructure / Okx — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Okx/RealOkxClient.cs
```cs
   27: public RealOkxClient(ILogger<RealOkxClient> logger, IOptions<OkxOptions> options)
   33: public BrokerKind Kind => BrokerKind.Okx;
   34: public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);
   36: public async Task ConnectAsync(CancellationToken ct = default)
   56: public Task DisconnectAsync(CancellationToken ct = default)
   62: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   72: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   95: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
   98: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  101: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
  104: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  107: public async ValueTask DisposeAsync()
```
