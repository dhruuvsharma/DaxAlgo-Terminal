# TradingTerminal.Infrastructure / Alpaca — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Alpaca/RealAlpacaClient.cs
```cs
   33: public sealed class RealAlpacaClient : IBrokerClient
   45: public RealAlpacaClient(ILogger<RealAlpacaClient> logger, IOptions<AlpacaOptions> options)
   51: public BrokerKind Kind => BrokerKind.Alpaca;
   53: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   55: public async Task ConnectAsync(CancellationToken ct = default)
  138: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  216: public async Task DisconnectAsync(CancellationToken ct = default)
  234: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  267: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  315: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  398: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  403: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  468: public async ValueTask DisposeAsync()
```
