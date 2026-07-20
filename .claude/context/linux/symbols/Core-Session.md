# TradingTerminal.Core / Session — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Session/SessionContext.cs
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
