# TradingTerminal.Infrastructure / CTrader — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/CTrader/CTraderAccountDiscoveryService.cs
```cs
   16: public sealed class CTraderAccountDiscoveryService : ICTraderAccountDiscovery
   22: public CTraderAccountDiscoveryService(ILogger<CTraderAccountDiscoveryService> logger)
   27: public async Task<IReadOnlyList<CTraderDiscoveredAccount>> DiscoverAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/CTrader/RealCTraderClient.cs
```cs
   26: public sealed class RealCTraderClient : IBrokerClient
   45: public RealCTraderClient(ILogger<RealCTraderClient> logger, IOptions<CTraderOptions> options)
   51: public BrokerKind Kind => BrokerKind.CTrader;
   53: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   55: public async Task ConnectAsync(CancellationToken ct = default)
  148: public Task DisconnectAsync(CancellationToken ct = default)
  162: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  257: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  293: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  341: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  404: public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  535: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  626: public ValueTask DisposeAsync()
  645: public MessageDispatcher(RealCTraderClient owner) => _owner = owner;
  647: public void OnNext(IMessage value)
  660: public void OnError(Exception error)
  666: public void OnCompleted()
```
