# TradingTerminal.Infrastructure / Crypto — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Pipeline/TradingTerminal.Infrastructure/Crypto/CryptoConvert.cs
```cs
   10: public static long ToSize(double qty, double scale) => (long)Math.Round(qty * scale);
   13: public static double D(JsonElement el) => el.ValueKind switch
   21: public static double D(JsonElement obj, string name) =>
   24: public static long MsToTicksUtc(JsonElement obj, string name)
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Crypto/CryptoStream.cs
```cs
   21: public static async IAsyncEnumerable<T> StreamAsync<T>(
```

## src/linux/Pipeline/TradingTerminal.Infrastructure/Crypto/L2OrderBook.cs
```cs
   19: public void Clear()
   26: public void Apply(bool isBid, double price, double size)
   33: public bool IsEmpty => _bids.Count == 0 && _asks.Count == 0;
   35: public DepthSnapshot Snapshot(int levels, double sizeScale, DateTime? timeUtc = null)
```
