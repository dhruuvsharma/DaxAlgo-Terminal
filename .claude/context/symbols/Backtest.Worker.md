# TradingTerminal.Backtest.Worker — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

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
   14: public async Task<BacktestResultManifest> PublishSuccessAsync(
   64: public async Task<BacktestResultManifest> PublishFailureAsync(
  200: public override bool CanRead => false;
  201: public override bool CanSeek => false;
  202: public override bool CanWrite => true;
  203: public override long Length => _written;
  204: public override long Position { get => _written; set => throw new NotSupportedException(); }
  205: public override void Flush() => inner.Flush();
  206: public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
  207: public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  208: public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  209: public override void SetLength(long value) => throw new NotSupportedException();
  211: public override void Write(byte[] buffer, int offset, int count)
  217: public override void Write(ReadOnlySpan<byte> buffer)
  223: public override async ValueTask WriteAsync(
  231: protected override void Dispose(bool disposing)
  236: public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
```

## src/windows/Backtest/TradingTerminal.Backtest.Worker/WorkerProgressEmitter.cs
```cs
   13: public async Task EmitAsync(
   55: public async Task RunHeartbeatAsync(long? eventsTotal, CancellationToken ct)
```
