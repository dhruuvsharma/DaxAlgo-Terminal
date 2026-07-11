# TradingTerminal.Infrastructure / LondonStrategicEdge — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/LondonStrategicEdge/RealLondonStrategicEdgeClient.cs
```cs
   67: public RealLondonStrategicEdgeClient(
   76: public BrokerKind Kind => BrokerKind.LondonStrategicEdge;
   78: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   82: public async Task ConnectAsync(CancellationToken ct = default)
  123: public async Task DisconnectAsync(CancellationToken ct = default)
  151: public async ValueTask DisposeAsync()
  161: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  232: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  322: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
  330: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  356: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  361: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  705: public Subscription(string symbol) => Symbol = symbol;
  707: public string Symbol { get; }
  708: public ChannelWriter<Tick> Writer => _channel.Writer;
  709: public ChannelReader<Tick> Reader => _channel.Reader;
  711: public void Complete() => _channel.Writer.TryComplete();
```
