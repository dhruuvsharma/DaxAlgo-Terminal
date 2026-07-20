# TradingTerminal.Infrastructure / Ib — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Ib/ConnectionManager.cs
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

## src/linux/Pipeline/TradingTerminal.Infrastructure/Ib/IbCuratedCatalog.cs
```cs
   48: public static IReadOnlyList<TradableInstrument> All { get; } = Build();
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Ib/RealIbClient.cs
```cs
   28: public sealed class RealIbClient : IBApi.DefaultEWrapper, IBrokerClient
   47: public RealIbClient(ILogger<RealIbClient> logger, IOptions<InteractiveBrokersOptions> options)
   53: public BrokerKind Kind => BrokerKind.InteractiveBrokers;
   55: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   57: public async Task ConnectAsync(CancellationToken ct = default)
  116: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
  119: public Task DisconnectAsync(CancellationToken ct = default)
  127: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  157: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  189: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  219: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  228: public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  292: public ValueTask DisposeAsync()
  302: public HistoricalRequest(TaskCompletionSource<IReadOnlyList<Bar>> tcs) { Tcs = tcs; }
  303: public TaskCompletionSource<IReadOnlyList<Bar>> Tcs { get; }
  304: public List<Bar> Bars { get; } = new();
  314: public Channel<Tick> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<Tick>();
  315: public double Bid;
  316: public double Ask;
  317: public long BidSize;
  318: public long AskSize;
  323: public override void nextValidId(int orderId)
  329: public override void historicalData(int reqId, IBApi.Bar bar)
  343: public override void historicalDataEnd(int reqId, string startDateStr, string endDateStr)
  350: public override void historicalDataUpdate(int reqId, IBApi.Bar bar)
  363: public override void tickPrice(int reqId, int field, double price, IBApi.TickAttrib attribs)
  395: public override void tickByTickAllLast(
  411: public override void tickSize(int reqId, int field, decimal size)
  425: public override void error(Exception e) =>
  428: public override void error(string str) =>
  431: public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
  460: public override void connectionClosed()
```
