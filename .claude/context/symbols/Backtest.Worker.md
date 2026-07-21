# TradingTerminal.Backtest.Worker — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Backtest/TradingTerminal.Backtest.Worker/BundleStrategyLoadContext.cs
```cs
   26: public Assembly LoadEngine(byte[] bytes)
   32: public void ClearAndUnload()
   38: protected override Assembly? Load(AssemblyName assemblyName)
   65: protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
   69: public static bool IsWorkerExternalAssemblyAvailable(string simpleName) =>
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/BundleStrategyLoader.cs
```cs
   20: public BacktestStrategyKernelAdapter Kernel { get; } = kernel;
   21: public string StrategyAssemblySha256 { get; } = strategyAssemblySha256;
   22: public IReadOnlyList<BacktestLoadedAssemblyFingerprint> Closure { get; } = closure;
   24: public async ValueTask DisposeAsync()
   40: public static async Task<BundleStrategyExecution> LoadAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/ParquetMarketDataFeed.cs
```cs
   14: public async IAsyncEnumerable<MarketEvent> StreamAsync(
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/WorkerApplication.cs
```cs
   13: public static async Task<int> RunAsync(string[] args)
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/WorkerArtifactPublisher.cs
```cs
   15: public static WorkerStrategyFingerprint Unknown { get; } = new(
   27: public async Task<BacktestResultManifest> PublishSuccessAsync(
   79: public async Task<BacktestResultManifest> PublishFailureAsync(
  223: public override bool CanRead => false;
  224: public override bool CanSeek => false;
  225: public override bool CanWrite => true;
  226: public override long Length => _written;
  227: public override long Position { get => _written; set => throw new NotSupportedException(); }
  228: public override void Flush() => inner.Flush();
  229: public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
  230: public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  231: public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  232: public override void SetLength(long value) => throw new NotSupportedException();
  234: public override void Write(byte[] buffer, int offset, int count)
  240: public override void Write(ReadOnlySpan<byte> buffer)
  246: public override async ValueTask WriteAsync(
  254: protected override void Dispose(bool disposing)
  259: public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/WorkerProgressEmitter.cs
```cs
   13: public async Task EmitAsync(
   55: public async Task RunHeartbeatAsync(long? eventsTotal, CancellationToken ct)
```
