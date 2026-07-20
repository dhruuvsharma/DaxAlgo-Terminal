# TradingTerminal.Backtest.Cli — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Backtest/TradingTerminal.Backtest.Cli/Args.cs
```cs
   14: public Args(string[] argv)
   40: public string Required(string name) =>
   44: public string? Optional(string name) =>
   47: public double Double(string name, double @default) =>
   52: public int Int(string name, int @default) =>
   57: public DateTime? Date(string name)
```

## src/linux/Backtest/TradingTerminal.Backtest.Cli/Output/ResultWriter.cs
```cs
   15: public static async Task WriteAsync(string outputDir, BacktestResult result, CancellationToken ct)
```

## src/linux/Backtest/TradingTerminal.Backtest.Cli/StoreFactory.cs
```cs
   21: public static IMarketDataStore Open(string? sqlitePath, string? postgresConn)
   55: public static InstrumentId? ResolveSymbol(string? sqlitePath, string? postgresConn, string symbol)
```
