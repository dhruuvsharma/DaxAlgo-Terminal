# TradingTerminal.Infrastructure / LondonStrategicEdge — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/LondonStrategicEdge/RealLondonStrategicEdgeClient.cs
```cs
   66: public RealLondonStrategicEdgeClient(
   75: public BrokerKind Kind => BrokerKind.LondonStrategicEdge;
   77: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   81: public async Task ConnectAsync(CancellationToken ct = default)
  122: public async Task DisconnectAsync(CancellationToken ct = default)
  150: public async ValueTask DisposeAsync()
  160: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  231: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  321: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
  329: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  355: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  360: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  704: public Subscription(string symbol) => Symbol = symbol;
  706: public string Symbol { get; }
  707: public ChannelWriter<Tick> Writer => _channel.Writer;
  708: public ChannelReader<Tick> Reader => _channel.Reader;
  710: public void Complete() => _channel.Writer.TryComplete();
```
