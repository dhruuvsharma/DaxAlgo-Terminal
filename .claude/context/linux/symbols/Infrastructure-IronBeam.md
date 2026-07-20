# TradingTerminal.Infrastructure / IronBeam — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/IronBeam/RealIronBeamClient.cs
```cs
   70: public RealIronBeamClient(ILogger<RealIronBeamClient> logger, IOptions<IronBeamOptions> options)
   76: public BrokerKind Kind => BrokerKind.IronBeam;
   78: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   82: public async Task ConnectAsync(CancellationToken ct = default)
  116: public async Task DisconnectAsync(CancellationToken ct = default)
  145: public async ValueTask DisposeAsync()
  154: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  166: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  181: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
  190: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  193: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  197: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  738: public Subscription(StreamKind kind, string symbol)
  744: public StreamKind Kind { get; }
  745: public string Symbol { get; }
  746: public ChannelWriter<object> Writer => _channel.Writer;
  747: public ChannelReader<object> Reader => _channel.Reader;
  749: public void Complete() => _channel.Writer.TryComplete();
```
