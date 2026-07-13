# TradingTerminal.Core / Trading — public API surface

Generated 2026-07-13. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Trading/IFeeModel.cs
```cs
   11: public interface IFeeModel
   19:     double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity);
   23: public enum LiquidityFlag
   30: public sealed class ZeroFeeModel : IFeeModel
   32: public static readonly ZeroFeeModel Instance = new();
   33: public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) => 0;
   41: public sealed class MakerTakerFeeModel : IFeeModel
   43: public double TakerFeePerUnit { get; }
   44: public double MakerRebatePerUnit { get; }
   46: public MakerTakerFeeModel(double takerFeePerUnit, double makerRebatePerUnit)
   52: public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) =>
   62: public sealed class BpsFeeModel : IFeeModel
   64: public double Bps { get; }
   66: public BpsFeeModel(double bps) { Bps = bps; }
   68: public double Fee(OrderSide side, long quantity, double price, LiquidityFlag liquidity) =>
```

## src/windows/Core/TradingTerminal.Core/Trading/IOrderRouter.cs
```cs
    9: public interface IOrderRouter
   15:     Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
   18:     Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default);
   21:     IObservable<OrderEvent> OrderEvents { get; }
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderEvent.cs
```cs
    8: public sealed record OrderEvent(
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderRequest.cs
```cs
   12: public sealed record OrderRequest(
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderResult.cs
```cs
    8: public sealed record OrderResult(
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderSide.cs
```cs
    3: public enum OrderSide
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderState.cs
```cs
    9: public enum OrderState
```

## src/windows/Core/TradingTerminal.Core/Trading/OrderType.cs
```cs
    3: public enum OrderType
```

## src/windows/Core/TradingTerminal.Core/Trading/TimeInForce.cs
```cs
    3: public enum TimeInForce
```
