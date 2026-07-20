# TradingTerminal.Core / Risk — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Risk/IRiskManager.cs
```cs
   12: public interface IRiskManager
   18:     (bool Allowed, string? RejectReason) Evaluate(OrderRequest request);
   26:     void RecordFill(string symbol, OrderEvent fillEvent);
```

## src/linux/Core/TradingTerminal.Core/Risk/RiskManager.cs
```cs
   20: public sealed class RiskManager : IRiskManager
   29: public RiskManager(RiskOptions options)
   35: public long PositionFor(string symbol) =>
   39: public double RealisedPnlToday => _realisedPnlToday;
   41: public (bool Allowed, string? RejectReason) Evaluate(OrderRequest request)
   59: public void RecordFill(string symbol, OrderEvent fillEvent)
```

## src/linux/Core/TradingTerminal.Core/Risk/RiskOptions.cs
```cs
    8: public sealed class RiskOptions
   10: public const string SectionName = "Risk";
   13: public long MaxPositionPerSymbol { get; set; }
   16: public double MaxDailyLoss { get; set; }
   23: public double DefaultContractMultiplier { get; set; } = 1.0;
```
