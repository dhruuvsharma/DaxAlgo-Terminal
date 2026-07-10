# TradingTerminal.Infrastructure / Upstox — public API surface

Generated 2026-07-10. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Upstox/RealUpstoxClient.cs
```cs
   67: public RealUpstoxClient(ILogger<RealUpstoxClient> logger, IOptions<UpstoxOptions> options)
   73: public BrokerKind Kind => BrokerKind.Upstox;
   75: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   83: public async Task ConnectAsync(CancellationToken ct = default)
  122: public async Task DisconnectAsync(CancellationToken ct = default)
  148: public async ValueTask DisposeAsync()
  158: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  214: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  273: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default)
  277: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  280: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
  283: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  573: public Subscription(StreamKind kind, string instrumentKey)
  582: public StreamKind Kind { get; }
  583: public string InstrumentKey { get; }
  584: public ChannelWriter<object> Writer => _channel.Writer;
  585: public ChannelReader<object> Reader => _channel.Reader;
  587: public void Complete() => _channel.Writer.TryComplete();
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Upstox/UpstoxAuthService.cs
```cs
   18: public UpstoxAuthService(ILogger<UpstoxAuthService> logger) => _logger = logger;
   20: public string BuildAuthorizationUrl(string baseUrl, string apiKey, string redirectUri)
   28: public async Task<string> ExchangeCodeForTokenAsync(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Upstox/UpstoxFeedDecoder.cs
```cs
   16: public string InstrumentKey { get; set; } = string.Empty;
   17: public double? Ltp { get; set; }
   18: public List<UpstoxLevel> Levels { get; } = new();
   52: public static IReadOnlyList<UpstoxFeed> Decode(byte[] data)
```
