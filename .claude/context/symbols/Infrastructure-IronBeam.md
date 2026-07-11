# TradingTerminal.Infrastructure / IronBeam — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/IronBeam/RealIronBeamClient.cs
```cs
   71: public RealIronBeamClient(ILogger<RealIronBeamClient> logger, IOptions<IronBeamOptions> options)
   77: public BrokerKind Kind => BrokerKind.IronBeam;
   79: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   83: public async Task ConnectAsync(CancellationToken ct = default)
  117: public async Task DisconnectAsync(CancellationToken ct = default)
  146: public async ValueTask DisposeAsync()
  155: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  167: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  182: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
  191: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  194: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  198: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  738: public Subscription(StreamKind kind, string symbol)
  752: public StreamKind Kind { get; }
  753: public string Symbol { get; }
  754: public ChannelWriter<object> Writer => _channel.Writer;
  755: public ChannelReader<object> Reader => _channel.Reader;
  757: public void Complete() => _channel.Writer.TryComplete();
```
