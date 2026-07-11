# TradingTerminal.Core / Session — public API surface

Generated 2026-07-11. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Session/SessionContext.cs
```cs
    7: public sealed class SessionContext
    9: public string? Username { get; private set; }
   10: public string AccountType { get; private set; } = "Paper";
   11: public DateTime? SignedInAtUtc { get; private set; }
   12: public bool IsAuthenticated { get; private set; }
   14: public event EventHandler? Changed;
   16: public void SetSignedIn(string? username, string accountType)
   25: public void Clear()
```
