# TradingTerminal.Infrastructure / Crypto — public API surface

Generated 2026-07-17. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Crypto/CryptoConvert.cs
```cs
   10: public static long ToSize(double qty, double scale) => (long)Math.Round(qty * scale);
   13: public static double D(JsonElement el) => el.ValueKind switch
   21: public static double D(JsonElement obj, string name) =>
   24: public static long MsToTicksUtc(JsonElement obj, string name)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Crypto/CryptoStream.cs
```cs
   21: public static async IAsyncEnumerable<T> StreamAsync<T>(
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Crypto/L2OrderBook.cs
```cs
   19: public void Clear()
   26: public void Apply(bool isBid, double price, double size)
   33: public bool IsEmpty => _bids.Count == 0 && _asks.Count == 0;
   35: public DepthSnapshot Snapshot(int levels, double sizeScale, DateTime? timeUtc = null)
```
