# TradingTerminal.Core / Domain — public API surface

Generated 2026-07-10. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Domain/AssetClass.cs
```cs
    8: public enum AssetClass
```

## src/windows/Core/TradingTerminal.Core/Domain/Bar.cs
```cs
    4: public sealed record Bar(
```

## src/windows/Core/TradingTerminal.Core/Domain/BarSize.cs
```cs
    3: public enum BarSize
   13: public static class BarSizeExtensions
   16: public static string ToIbString(this BarSize size) => size switch
   27: public static TimeSpan ToTimeSpan(this BarSize size) => size switch
   38: public static string ToDisplayString(this BarSize size) => size switch
   49: public static BarSize FromDisplayString(string s) => s switch
```

## src/windows/Core/TradingTerminal.Core/Domain/ConnectionState.cs
```cs
    3: public enum ConnectionState
```

## src/windows/Core/TradingTerminal.Core/Domain/Contract.cs
```cs
    4: public sealed record Contract(
   11: public static Contract UsStock(string symbol, string primaryExchange = "NASDAQ") =>
```

## src/windows/Core/TradingTerminal.Core/Domain/DepthLevel.cs
```cs
    8: public sealed record DepthLevel(double Price, long Size);
```

## src/windows/Core/TradingTerminal.Core/Domain/DepthSnapshot.cs
```cs
   14: public sealed record DepthSnapshot(
   20: public double BestBid => Bids.Count > 0 ? Bids[0].Price : 0;
   23: public double BestAsk => Asks.Count > 0 ? Asks[0].Price : 0;
   26: public long BestBidSize => Bids.Count > 0 ? Bids[0].Size : 0;
   29: public long BestAskSize => Asks.Count > 0 ? Asks[0].Size : 0;
```

## src/windows/Core/TradingTerminal.Core/Domain/Instrument.cs
```cs
   11: public sealed record Instrument(
   21: public static Instrument New(
   37: public sealed record InstrumentAlias(
```

## src/windows/Core/TradingTerminal.Core/Domain/InstrumentId.cs
```cs
   10: public readonly record struct InstrumentId(int Value)
   13: public static InstrumentId None => new(0);
   15: public bool IsNone => Value == 0;
   17: public override string ToString() => $"#{Value}";
```

## src/windows/Core/TradingTerminal.Core/Domain/MarketDataRecords.cs
```cs
    6: public enum AggressorSide
   27: public sealed record Quote(
   39: public double Mid => (Bid + Ask) * 0.5;
   40: public double Spread => Ask - Bid;
   47: public sealed record TradePrint(
   63: public sealed record OhlcvBar(
   76: public static OhlcvBar FromBar(Bar bar, InstrumentId id, BarSize size, BrokerKind source, bool isFinal) =>
   80: public Bar ToBar() => new(OpenTimeUtc, Open, High, Low, Close, Volume);
```

## src/windows/Core/TradingTerminal.Core/Domain/Tick.cs
```cs
    8: public sealed record Tick(
   22: public sealed record TradeTick(
```
