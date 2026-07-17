# TradingTerminal.Infrastructure / Brokers — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Brokers/BrokerApiMeter.cs
```cs
   15: public sealed class BrokerApiMeter : IBrokerApiMeter
   38: public long Total;
   39: public DateTime? LastCallUtc;
   40: public readonly ConcurrentQueue<DateTime> Window = new();
   45: public void RecordCall(BrokerKind broker, string method)
   54: public IReadOnlyList<BrokerApiUsage> Snapshot()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Brokers/BrokerSelector.cs
```cs
   14: public sealed class BrokerSelector : IBrokerSelector, IAsyncDisposable
   22: public BrokerSelector(
   49: public IReadOnlyList<BrokerKind> AvailableKinds { get; }
   51: public bool IsAvailable(BrokerKind kind) => _clients.ContainsKey(kind);
   53: public IReadOnlyList<BrokerKind> Connected
   68: public bool IsConnected(BrokerKind kind) =>
   71: public IBrokerClient Get(BrokerKind kind)
   79: public BrokerConnectionMode ModeOf(BrokerKind kind)
   86: public IObservable<ConnectionState> StateOf(BrokerKind kind)
   93: public ConnectionState CurrentStateOf(BrokerKind kind)
  100: public event EventHandler<BrokerStateChangedEventArgs>? StateChanged;
  102: public Task ConnectAsync(BrokerKind kind, CancellationToken ct = default)
  110: public Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default)
  118: public async ValueTask DisposeAsync()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Brokers/MeteredBrokerClient.cs
```cs
   18: public sealed class MeteredBrokerClient : IBrokerClient
   23: public MeteredBrokerClient(IBrokerClient inner, IBrokerApiMeter meter)
   29: public BrokerKind Kind => _inner.Kind;
   30: public IObservable<ConnectionState> ConnectionState => _inner.ConnectionState;
   32: public Task ConnectAsync(CancellationToken ct = default)
   38: public Task DisconnectAsync(CancellationToken ct = default)
   44: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   50: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
   57: public IAsyncEnumerable<Bar> SubscribeBarsAsync(
   64: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
   70: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
   77: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default)
   83: public ValueTask DisposeAsync() => _inner.DisposeAsync();
```
