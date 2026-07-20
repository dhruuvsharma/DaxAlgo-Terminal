# TradingTerminal.Infrastructure / NinjaTrader — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/NinjaTrader/RealNinjaClient.cs
```cs
   29: public sealed class RealNinjaClient : IBrokerClient
   39: public RealNinjaClient(ILogger<RealNinjaClient> logger, IOptions<NinjaTraderOptions> options)
   45: public BrokerKind Kind => BrokerKind.NinjaTrader;
   47: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   49: public Task ConnectAsync(CancellationToken ct = default)
   83: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default) =>
   86: public Task DisconnectAsync(CancellationToken ct = default)
  102: public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  135: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  192: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  236: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  245: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  298: public ValueTask DisposeAsync()
  318: public static extern int Connected(int showMessage);
  321: public static extern int SubscribeMarketData([MarshalAs(UnmanagedType.LPStr)] string instrument);
  324: public static extern int UnsubscribeMarketData([MarshalAs(UnmanagedType.LPStr)] string instrument);
  327: public static extern double Bid([MarshalAs(UnmanagedType.LPStr)] string instrument);
  330: public static extern double Ask([MarshalAs(UnmanagedType.LPStr)] string instrument);
  333: public static extern double LastPrice([MarshalAs(UnmanagedType.LPStr)] string instrument);
  336: public static extern long Volume([MarshalAs(UnmanagedType.LPStr)] string instrument);
  339: public static extern int Command(
```
