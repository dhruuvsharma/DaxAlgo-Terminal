# TradingTerminal.MarketData — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/ArchiveBundleBuilder.cs
```cs
   27: public ArchiveBundleBuilder(IMarketDataStore store, IInstrumentRegistry registry, ILogger logger)
   34: public async Task<BundleResult> BuildAsync(
  300: public static async Task<List<BundlePart>> SplitFileAsync(
  340: public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/ArchiveManifestStore.cs
```cs
   19: public ArchiveManifestStore(string databasePath)
   75: public long Insert(ArchiveManifestEntry entry)
  106: public void MarkLocalDeleted(long id)
  114: public IReadOnlyList<ArchiveManifestEntry> List(string? transport, int maxRows)
  133: public long? FindCovering(DateTime fromUtc, DateTime toUtc, string transport)
  150: public ArchiveManifestEntry? FindOverlapping(DateTime fromUtc, DateTime toUtc)
  196: public void Dispose() => _writeConnection.Dispose();
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/ArchiveRecords.cs
```cs
    7: public long InstrumentId { get; set; }
    8: public long EventTimeMicros { get; set; }
    9: public long IngestTimeMicros { get; set; }
   10: public double Bid { get; set; }
   11: public double Ask { get; set; }
   12: public long BidSize { get; set; }
   13: public long AskSize { get; set; }
   14: public int Source { get; set; }
   15: public long Sequence { get; set; }
   16: public bool EventTimeApproximate { get; set; }
   22: public long InstrumentId { get; set; }
   23: public int BarSize { get; set; }
   24: public long OpenTimeMicros { get; set; }
   25: public double Open { get; set; }
   26: public double High { get; set; }
   27: public double Low { get; set; }
   28: public double Close { get; set; }
   29: public long Volume { get; set; }
   30: public int Source { get; set; }
   31: public bool IsFinal { get; set; }
   37: public long InstrumentId { get; set; }
   38: public long EventTimeMicros { get; set; }
   39: public long IngestTimeMicros { get; set; }
   40: public double Price { get; set; }
   41: public long Size { get; set; }
   42: public int Aggressor { get; set; }
   43: public int Source { get; set; }
   44: public long Sequence { get; set; }
   45: public bool EventTimeApproximate { get; set; }
   53: public long InstrumentId { get; set; }
   54: public long EventTimeMicros { get; set; }
   55: public long IngestTimeMicros { get; set; }
   56: public string Side { get; set; } = string.Empty;       // "B" (bid) | "A" (ask)
   57: public int Level { get; set; }                          // 0 = best
   58: public double Price { get; set; }
   59: public long Size { get; set; }
   60: public int Source { get; set; }
   71: public const string Format = "format";
   72: public const string PerDocument = "perdoc";
   73: public const string Kind = "kind";            // quotes | bars | trades | depth
   74: public const string DocKey = "doc";           // groups the parts of one logical document
   75: public const string PartIndex = "part";       // 1-based slice index within the document
   76: public const string PartCount = "parts";      // total slices in the document
   77: public const string InstrumentId = "instrument_id";
   78: public const string Symbol = "symbol";
   79: public const string Exchange = "exchange";
   80: public const string Broker = "broker";
   81: public const string BarSize = "bar_size";
   82: public const string Rows = "rows";
   90: public int Version { get; set; } = 1;
   91: public DateTime FromUtc { get; set; }
   92: public DateTime ToUtc { get; set; }
   93: public long RowsQuotes { get; set; }
   94: public long RowsBars { get; set; }
   95: public long RowsTrades { get; set; }
   96: public long RowsDepth { get; set; }
   97: public List<BundleFile> Files { get; set; } = new();
  102: public string Path { get; set; } = string.Empty;
  103: public string Kind { get; set; } = string.Empty;     // "quotes" | "bars" | "trades" | "depth"
  104: public long InstrumentId { get; set; }
  105: public int BarSize { get; set; }                       // only meaningful for bars
  106: public long Rows { get; set; }
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/ArchiveScheduleService.cs
```cs
   28: public ArchiveScheduleService(
   38: public Task StartAsync(CancellationToken cancellationToken)
   50: public Task StopAsync(CancellationToken cancellationToken)
   87: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/ArchiveServiceCollectionExtensions.cs
```cs
   11: public static class ArchiveServiceCollectionExtensions
   21: public static IServiceCollection AddMarketDataArchive(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/Lake/LocalParquetLakeExporter.cs
```cs
   27: public sealed class LocalParquetLakeExporter
   33: public LocalParquetLakeExporter(
   43: public async Task<ParquetLakeExportResult> ExportRangeAsync(
  170: public sealed class ParquetLakeExportResult
  172: public int FilesWritten { get; set; }
  173: public int FilesSkipped { get; set; }
  174: public long Rows { get; set; }
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/Lake/ParquetLakeExportService.cs
```cs
   26: public ParquetLakeExportService(
   36: public Task StartAsync(CancellationToken cancellationToken)
   45: public Task StopAsync(CancellationToken cancellationToken)
   96: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/Lake/ParquetLakeServiceCollectionExtensions.cs
```cs
    8: public static class ParquetLakeServiceCollectionExtensions
   16: public static IServiceCollection AddParquetLake(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/MarketDataArchiver.cs
```cs
   38: public MarketDataArchiver(
   54: public async Task<ArchiveResult> ArchiveRangeAsync(
  191: public Task<IReadOnlyList<ArchiveManifestEntry>> ListArchivesAsync(
  195: public async Task<IReadOnlyList<ArchiveCoverageWindow>> GetCoverageAsync(CancellationToken ct = default)
  202: public async Task<InstantOffloadResult> OffloadPendingAsync(IProgress<string>? progress, CancellationToken ct)
  275: public async Task RestoreAsync(
  548: public static string BuildPeriodLabel(DateTime fromUtc, DateTime toUtc, ArchivePeriod period) =>
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/Telegram/TelegramArchiveTransport.cs
```cs
   23: public sealed class TelegramArchiveTransport : IArchiveTransport, IAsyncDisposable
   35: public TelegramArchiveTransport(
   45: public string Name => "telegram";
   47: public bool IsReady => _client is not null && _selfUser is not null;
   53: public Task EnsureConnectedAsync(CancellationToken ct = default) =>
   61: public async Task EnsureConnectedAsync(TelegramArchiveOptions opts, CancellationToken ct = default)
  141: public async Task<ArchiveBlobRef> UploadAsync(
  189: public async Task DownloadAsync(
  245: public async ValueTask DisposeAsync()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Archive/Telegram/TelegramAuthPrompt.cs
```cs
    9: public interface ITelegramAuthPrompt
   11:     Task<string?> PromptAsync(string key, CancellationToken ct);
   16: public sealed class NullTelegramAuthPrompt : ITelegramAuthPrompt
   18: public Task<string?> PromptAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
```

## src/windows/Pipeline/TradingTerminal.MarketData/InstrumentDiscoveryService.cs
```cs
   34: public InstrumentDiscoveryService(
   44: public Task StartAsync(CancellationToken cancellationToken)
   58: public Task StopAsync(CancellationToken cancellationToken)
  118: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/MarketDataHub.cs
```cs
   23: public IObservable<Quote> Quotes(InstrumentId instrumentId) =>
   26: public IObservable<TradePrint> Trades(InstrumentId instrumentId) =>
   29: public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
   32: public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) =>
   35: public void PublishQuote(Quote quote) =>
   38: public void PublishTrade(TradePrint trade) =>
   41: public void PublishBar(OhlcvBar bar) =>
   44: public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) =>
```

## src/windows/Pipeline/TradingTerminal.MarketData/MarketDataIngestService.cs
```cs
   40: public required CancellationTokenSource Cts { get; init; }
   41: public int RefCount;
   42: public long Sequence;
   62: public double Bid;
   63: public double Ask;
   64: public double PriorTradePrice;
   65: public AggressorSide PriorClassification = AggressorSide.Unknown;
   69: public MarketDataIngestService(
   83: public InstrumentId Resolve(Contract contract, BrokerKind broker) =>
   86: public IDisposable Subscribe(Contract contract, BrokerKind broker)
   98: public IDisposable SubscribeBars(Contract contract, BrokerKind broker, BarSize size)
  107: public IDisposable SubscribeTrades(Contract contract, BrokerKind broker)
  240: public Handle(MarketDataIngestService owner, (int, BrokerKind, string) key, Entry _)
  246: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/MarketDataPipelineServiceCollectionExtensions.cs
```cs
   16: public static class MarketDataPipelineServiceCollectionExtensions
   25: public static IServiceCollection AddMarketDataPipeline(this IServiceCollection services, IConfiguration configuration)
```

## src/windows/Pipeline/TradingTerminal.MarketData/MarketDataRepository.cs
```cs
   31: public sealed class MarketDataRepository : IMarketDataRepository
   40: public MarketDataRepository(
   56: public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
   81: public async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
  119: public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
  159: public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
  200: public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/CompositeMarketDataStore.cs
```cs
   20: public CompositeMarketDataStore(IMarketDataStore tickStore, IMarketDataStore barStore, ILogger logger)
   27: public void EnqueueQuote(Quote quote) => _tickStore.EnqueueQuote(quote);
   28: public void EnqueueTrade(TradePrint trade) => _tickStore.EnqueueTrade(trade);
   29: public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source) =>
   31: public void EnqueueBar(OhlcvBar bar) => _barStore.EnqueueBar(bar);
   33: public async Task FlushAsync(CancellationToken ct = default)
   39: public async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
   46: public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
   50: public IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
   54: public IAsyncEnumerable<Quote> ReadQuotesAsync(
   58: public IAsyncEnumerable<TradePrint> ReadTradesAsync(
   62: public IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
   66: public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
   69: public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
   72: public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
   75: public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
   79: public bool IsActive => (_tickStore as IReactivatableTickStore)?.IsActive ?? true;
   80: public bool TryActivate() => (_tickStore as IReactivatableTickStore)?.TryActivate() ?? true;
   82: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/EpochTime.cs
```cs
   12: public static long ToMicros(DateTime utc)
   18: public static DateTime FromMicros(long micros) =>
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/InstrumentRegistry.cs
```cs
   24: public InstrumentRegistry(IInstrumentPersistence persistence, ILogger logger)
   32: public Instrument? Get(InstrumentId id)
   37: public InstrumentId? Resolve(BrokerKind broker, string brokerSymbol)
   43: public InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker)
   73: public string? ToBrokerSymbol(InstrumentId id, BrokerKind broker)
   83: public void RegisterAlias(InstrumentAlias alias)
   92: public IReadOnlyList<Instrument> All()
  123: public void Dispose() => _persistence.Dispose();
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/MarketDataStoreBase.cs
```cs
   20: protected enum WriteKind { Quote, Trade, Bar, Depth }
   24: protected sealed record DepthRecord(InstrumentId InstrumentId, DepthSnapshot Snapshot, BrokerKind Source, DateTime IngestTimeUtc);
   26: protected readonly record struct WriteOp(WriteKind Kind, Quote? Quote, TradePrint? Trade, OhlcvBar? Bar, DepthRecord? Depth = null);
   35: protected MarketDataStoreBase(bool persist, int batchSize, ILogger logger)
   48: protected void StartWriter() => _writerLoop = Task.Run(() => RunWriterAsync(_cts.Token));
   53: protected void EnablePersistence() => _persist = true;
   55: public void EnqueueQuote(Quote quote)
   60: public void EnqueueTrade(TradePrint trade)
   65: public void EnqueueBar(OhlcvBar bar)
   70: public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source)
   80: public virtual Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) =>
   83: public async Task FlushAsync(CancellationToken ct = default)
  125: protected abstract void WriteBatch(IReadOnlyList<WriteOp> batch);
  127: public abstract Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
  131: public abstract IAsyncEnumerable<Quote> ReadQuotesAsync(
  135: public abstract IAsyncEnumerable<TradePrint> ReadTradesAsync(
  140: public virtual async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
  148: public abstract IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
  152: public abstract Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
  153: public abstract Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
  154: public abstract Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
  157: public virtual Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  160: public void Dispose()
  170: protected virtual void OnDispose() { }
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/NpgsqlInstrumentPersistence.cs
```cs
   17: public NpgsqlInstrumentPersistence(string connectionString, ILogger logger)
   23: public void EnsureSchema()
   30: public IReadOnlyList<Instrument> LoadInstruments()
   45: public IReadOnlyList<(BrokerKind Broker, string Symbol, InstrumentId Id)> LoadAliases()
   57: public InstrumentId UpsertInstrument(Instrument ins)
   76: public void UpsertAlias(InstrumentAlias alias)
   93: public void Dispose() { /* no long-lived connection — pooled per operation */ }
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/NpgsqlMarketDataStore.cs
```cs
   20: public NpgsqlMarketDataStore(
   36: protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
  114: public override async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
  133: public override async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
  163: public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
  190: public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
  218: public override Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  221: public override Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  224: public override Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  237: public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/PerBrokerSqliteMarketDataStore.cs
```cs
   45: public PerBrokerSqliteMarketDataStore(
   66: public void EnqueueQuote(Quote quote) => StoreFor(quote.Source, Stream.Quotes).EnqueueQuote(quote);
   67: public void EnqueueTrade(TradePrint trade) => StoreFor(trade.Source, Stream.Trades).EnqueueTrade(trade);
   68: public void EnqueueBar(OhlcvBar bar) => StoreFor(bar.Source, Stream.Bars).EnqueueBar(bar);
   69: public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source) =>
   72: public async Task FlushAsync(CancellationToken ct = default) =>
   76: public async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
   93: public IAsyncEnumerable<Quote> ReadQuotesAsync(
  100: public IAsyncEnumerable<TradePrint> ReadTradesAsync(
  107: public IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
  115: public IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
  119: public async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
  128: public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  131: public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  134: public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  137: public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  262: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/QuestDbDockerBootstrapper.cs
```cs
   25: public static TimeSpan StartupTimeout(MarketDataStoreOptions opts) =>
   29: public static bool DockerCliPresent() => TryRunDocker("--version", TimeSpan.FromSeconds(15), out _, log: null);
   32: public static bool DockerDaemonReady() => TryRunDocker("info", TimeSpan.FromSeconds(20), out _, log: null);
   40: public static bool TryStartDockerEngineCli(ILogger log)
   53: public static bool IsReachable(string conn)
   70: public static bool TryStartContainer(MarketDataStoreOptions opts, ILogger log)
   88: public static bool TryLaunchDockerDesktop(MarketDataStoreOptions opts, ILogger log)
  108: public static bool WaitForDaemon(TimeSpan timeout, CancellationToken ct)
  120: public static bool WaitUntilReachable(string conn, TimeSpan timeout, CancellationToken ct)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/QuestDbDockerService.cs
```cs
   18: public sealed class QuestDbDockerService : IQuestDbLauncher
   24: public QuestDbDockerService(
   32: public bool IsQuestDbBackend => _opts.Provider == MarketDataProvider.QuestDb;
   35: public bool IsApplicable => IsQuestDbBackend;
   36: public bool AutoStart => _opts.AutoStartDocker;
   37: public bool IsReachable() => QuestDbDockerBootstrapper.IsReachable(_opts.QuestDbPgConnectionString);
   41: public Task<bool> StartAsync(CancellationToken ct = default) => Task.Run(() => StartCore(ct), ct);
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/QuestDbMarketDataStore.cs
```cs
   41: public QuestDbMarketDataStore(
   60: public bool IsActive => _available;
   66: public bool TryActivate()
   89: protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
  151: public override async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
  179: public override Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
  184: public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
  192: public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
  210: public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
  228: public override async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
  264: public override Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  267: public override Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  270: public override Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  273: public override Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  323: protected override void OnDispose() => _sender?.Dispose();
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/QuestDbSchema.cs
```cs
   16: public static void EnsureCreated(string pgConnectionString, int depthRetentionDays, ILogger logger)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteInstrumentPersistence.cs
```cs
   13: public SqliteInstrumentPersistence(string connectionString)
   20: public void EnsureSchema() => SqliteSchema.EnsureCreated(_connection);
   22: public IReadOnlyList<Instrument> LoadInstruments()
   35: public IReadOnlyList<(BrokerKind Broker, string Symbol, InstrumentId Id)> LoadAliases()
   46: public InstrumentId UpsertInstrument(Instrument ins)
   64: public void UpsertAlias(InstrumentAlias alias)
   80: public void Dispose() => _connection.Dispose();
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteMarketDataStore.cs
```cs
   29: public SqliteMarketDataStore(
   60: protected override void WriteBatch(IReadOnlyList<WriteOp> batch)
  212: public override async Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default)
  239: public override async Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(
  270: public override async IAsyncEnumerable<Quote> ReadQuotesAsync(
  299: public override async IAsyncEnumerable<TradePrint> ReadTradesAsync(
  328: public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
  359: public override async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(
  398: public override Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  401: public override Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  404: public override Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  407: public override Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
  434: protected override void OnDispose() => _writeConnection.Dispose();
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteModelRegistry.cs
```cs
   31: public SqliteModelRegistry(string databasePath)
   74: public StoredModel Save(ModelArtifact artifact)
  130: public ModelArtifact? Load(string modelId)
  141: public ModelArtifact? LoadLatest(ModelKey key)
  159: public IReadOnlyList<StoredModelInfo> List(ModelKey? filter, int maxRows)
  186: public bool Delete(string modelId)
  197: public int PruneOlderThan(int retentionDays)
  255: public void Dispose()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteSchema.cs
```cs
   15: public static void ApplyPragmas(SqliteConnection cn)
   30: public static void EnsureCreated(SqliteConnection cn)
   58: public static void EnsureQuotesCreated(SqliteConnection cn) => Exec(cn, """
   75: public static void EnsureTradesCreated(SqliteConnection cn) => Exec(cn, """
   91: public static void EnsureBarsCreated(SqliteConnection cn) => Exec(cn, """
  110: public static void EnsureDepthCreated(SqliteConnection cn) => Exec(cn, """
```

## src/windows/Pipeline/TradingTerminal.MarketData/Store/TimescaleSchema.cs
```cs
   15: public static DateTime Utc(DateTime d) => d.Kind switch
   24: public static void ApplyRetention(
   61: public static void EnsureCreated(NpgsqlConnection cn, ILogger logger)
```

## src/windows/Pipeline/TradingTerminal.MarketData/Threading/FeedChannel.cs
```cs
   15: public static class FeedChannel
   20: public static class Capacity
   23: public const int Quotes = 16_384;
   27: public const int Bars = 8_192;
   30: public const int Trades = 65_536;
   34: public const int Depth = 2_048;
   40: public static Channel<T> CreateDropOldest<T>(
   64: public sealed class FeedDropMeter
   74: public long Dropped => Interlocked.Read(ref _dropped);
   78: public static long GlobalDropped => Interlocked.Read(ref _globalDropped);
   82: public bool Record()
```

## src/windows/Pipeline/TradingTerminal.MarketData/Threading/IUiDispatcher.cs
```cs
    7: public interface IUiDispatcher
   10:     bool CheckAccess();
   13:     void Post(Action action);
   16:     Task InvokeAsync(Action action);
```

## src/windows/Pipeline/TradingTerminal.MarketData/Threading/ImmediateUiDispatcher.cs
```cs
    9: public sealed class ImmediateUiDispatcher : IUiDispatcher
   11: public bool CheckAccess() => true;
   13: public void Post(Action action) => action();
   15: public Task InvokeAsync(Action action)
```
