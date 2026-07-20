# TradingTerminal.StrategyComposer — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/UI/TradingTerminal.StrategyComposer/AuthoredStrategyViewComposer.cs
```cs
   14: public sealed class AuthoredStrategyViewComposer(IServiceProvider services) : IAuthoredStrategyViewComposer
   18: public object ComposeView(ITradingStrategy descriptor) => new ComposedStrategyView(descriptor, services);
   22: public static class StrategyComposerServiceCollectionExtensions
   27: public static IServiceCollection AddStrategyViewComposer(this IServiceCollection services)
```

## src/windows/UI/TradingTerminal.StrategyComposer/ComposedStrategyView.xaml.cs
```cs
   36: public partial class ComposedStrategyView : UserControl, IDisposable
   50: public ComposedStrategyView(ITradingStrategy descriptor, IServiceProvider services)
  100: public IReadOnlyList<FrameworkElement> Panels { get; private set; } = [];
  278: public void Dispose()
```
