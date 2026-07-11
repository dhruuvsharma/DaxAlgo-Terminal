# TradingTerminal.Infrastructure / Ib — public API surface

Generated 2026-07-12. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/ConnectionManager.cs
```cs
   24: public sealed class ConnectionManager : IAsyncDisposable
   48: public ConnectionManager(IBrokerClient client, ILogger<ConnectionManager> logger)
   63: public BrokerKind Broker => _client.Kind;
   66: public void ConfigureBackoff(TimeSpan initial, TimeSpan max)
   74: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   76: public ConnectionState CurrentState => _state.Value;
   78: public async Task StartAsync(CancellationToken ct = default)
   93: public async Task StopAsync(CancellationToken ct = default)
  187: public async ValueTask DisposeAsync()
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/IbCuratedCatalog.cs
```cs
   48: public static IReadOnlyList<TradableInstrument> All { get; } = Build();
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/RealIbClient.cs
```cs
   29: public sealed class RealIbClient : IBApi.DefaultEWrapper, IBrokerClient
   48: public RealIbClient(ILogger<RealIbClient> logger, IOptions<InteractiveBrokersOptions> options)
   54: public BrokerKind Kind => BrokerKind.InteractiveBrokers;
   56: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   58: public async Task ConnectAsync(CancellationToken ct = default)
  117: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
  120: public Task DisconnectAsync(CancellationToken ct = default)
  128: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  158: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  197: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  227: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  236: public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  307: public ValueTask DisposeAsync()
  317: public HistoricalRequest(TaskCompletionSource<IReadOnlyList<Bar>> tcs) { Tcs = tcs; }
  318: public TaskCompletionSource<IReadOnlyList<Bar>> Tcs { get; }
  319: public List<Bar> Bars { get; } = new();
  329: public Channel<Tick> Channel { get; } =
  331: public double Bid;
  332: public double Ask;
  333: public long BidSize;
  334: public long AskSize;
  339: public override void nextValidId(int orderId)
  345: public override void historicalData(int reqId, IBApi.Bar bar)
  359: public override void historicalDataEnd(int reqId, string startDateStr, string endDateStr)
  366: public override void historicalDataUpdate(int reqId, IBApi.Bar bar)
  379: public override void tickPrice(int reqId, int field, double price, IBApi.TickAttrib attribs)
  411: public override void tickByTickAllLast(
  427: public override void tickSize(int reqId, int field, decimal size)
  441: public override void error(Exception e) =>
  444: public override void error(string str) =>
  447: public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
  476: public override void connectionClosed()
```
