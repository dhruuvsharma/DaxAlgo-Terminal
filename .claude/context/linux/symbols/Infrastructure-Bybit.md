# TradingTerminal.Infrastructure / Bybit — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Bybit/RealBybitClient.cs
```cs
   26: public RealBybitClient(ILogger<RealBybitClient> logger, IOptions<BybitOptions> options)
   32: public BrokerKind Kind => BrokerKind.Bybit;
   33: public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);
   35: public async Task ConnectAsync(CancellationToken ct = default)
   55: public Task DisconnectAsync(CancellationToken ct = default)
   61: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   71: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   95: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
   98: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  101: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
  107: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  110: public async ValueTask DisposeAsync()
```
