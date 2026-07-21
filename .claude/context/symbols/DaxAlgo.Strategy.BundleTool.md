# DaxAlgo.Strategy.BundleTool — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Tools/DaxAlgo.Strategy.BundleTool/Program.cs
```cs
   42: public static int Run(string[] arguments)
  693: public static ParsedOptions Parse(IEnumerable<string> arguments)
  743: public void RequireOnly(IReadOnlySet<string> allowed)
  749: public string RequiredSingle(string name) =>
  752: public string? OptionalSingle(string name)
  762: public IReadOnlyList<string> Many(string name) =>
  770: public BundleSignatureException(string message) : base(message) { }
  771: public BundleSignatureException(string message, Exception innerException) : base(message, innerException) { }
```
