# TradingTerminal.Infrastructure / Simulation — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Simulation/SimulatedBrokerClient.cs
```cs
   35: public SimulatedBrokerClient(
   47: public BrokerKind Kind => BrokerKind.Simulated;
   49: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   51: public Task ConnectAsync(CancellationToken ct = default)
   59: public Task DisconnectAsync(CancellationToken ct = default)
   65: public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   85: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  112: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  152: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  190: public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
  228: public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
  405: public ValueTask DisposeAsync()
  420: public double Step(double vol)
  430: public Bar NextBar(DateTime timestampUtc, double vol)
  442: public long NextSize() => _rng.Next(1, 50);
  444: public bool NextBool() => _rng.Next(2) == 0;
```
