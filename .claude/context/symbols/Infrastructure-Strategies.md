# TradingTerminal.Infrastructure / Strategies — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/AuthoredStrategyInstaller.cs
```cs
   21: public sealed record AuthoredStrategyInstall(
   45: public sealed class AuthoredStrategyInstaller(
   53: public AuthoredStrategyInstall Install(StrategyScript script, StrategyCompileResult compiled)
```

## src/windows/Pipeline/TradingTerminal.Infrastructure/Strategies/Authoring/RoslynStrategyCompiler.cs
```cs
   25: public sealed class RoslynStrategyCompiler : IStrategyCompiler
   57: public StrategyCompileResult Compile(StrategyScript script)
  153: public sealed class DaxAlgoAuthoredPlugin : DaxAlgo.Sdk.IStrategyPlugin
  155: public string Name => {{Literal(script.DisplayName)}};
  156: public string TargetSdkVersion => {{Literal(SdkInfo.Version)}};
  158: public void Register(DaxAlgo.Sdk.IPluginRegistrar registrar) =>
```
