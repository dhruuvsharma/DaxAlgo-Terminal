# TradingTerminal.Infrastructure / Upstox — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Upstox/RealUpstoxClient.cs
```cs
   66: public RealUpstoxClient(ILogger<RealUpstoxClient> logger, IOptions<UpstoxOptions> options)
   72: public BrokerKind Kind => BrokerKind.Upstox;
   74: public IObservable<ConnectionState> ConnectionState => _state.AsObservable();
   82: public async Task ConnectAsync(CancellationToken ct = default)
  121: public async Task DisconnectAsync(CancellationToken ct = default)
  147: public async ValueTask DisposeAsync()
  157: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
  213: public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
  272: public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default)
  276: public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
  279: public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
  282: public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
  573: public Subscription(StreamKind kind, string instrumentKey)
  579: public StreamKind Kind { get; }
  580: public string InstrumentKey { get; }
  581: public ChannelWriter<object> Writer => _channel.Writer;
  582: public ChannelReader<object> Reader => _channel.Reader;
  584: public void Complete() => _channel.Writer.TryComplete();
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Upstox/UpstoxAuthService.cs
```cs
   18: public UpstoxAuthService(ILogger<UpstoxAuthService> logger) => _logger = logger;
   20: public string BuildAuthorizationUrl(string baseUrl, string apiKey, string redirectUri)
   28: public async Task<string> ExchangeCodeForTokenAsync(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Upstox/UpstoxFeedDecoder.cs
```cs
   16: public string InstrumentKey { get; set; } = string.Empty;
   17: public double? Ltp { get; set; }
   18: public List<UpstoxLevel> Levels { get; } = new();
   52: public static IReadOnlyList<UpstoxFeed> Decode(byte[] data)
```
