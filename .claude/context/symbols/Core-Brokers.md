# TradingTerminal.Core / Brokers — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Brokers/BrokerApiUsage.cs
```cs
   17: public sealed record BrokerApiUsage(
```

## src/windows/Core/TradingTerminal.Core/Brokers/BrokerConnectionMode.cs
```cs
   12: public sealed record BrokerConnectionMode(
```

## src/windows/Core/TradingTerminal.Core/Brokers/BrokerKind.cs
```cs
    3: public enum BrokerKind
```

## src/windows/Core/TradingTerminal.Core/Brokers/CTrader/CTraderDiscoveredAccount.cs
```cs
    8: public sealed record CTraderDiscoveredAccount(
```

## src/windows/Core/TradingTerminal.Core/Brokers/CTrader/ICTraderAccountDiscovery.cs
```cs
    9: public interface ICTraderAccountDiscovery
   21:     Task<IReadOnlyList<CTraderDiscoveredAccount>> DiscoverAsync(
   22:     string host,
   23:     int port,
   24:     string clientId,
   25:     string clientSecret,
   26:     string accessToken,
   27:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Brokers/IBrokerApiMeter.cs
```cs
   12: public interface IBrokerApiMeter
   15:     void RecordCall(BrokerKind broker, string method);
   18:     IReadOnlyList<BrokerApiUsage> Snapshot();
```

## src/windows/Core/TradingTerminal.Core/Brokers/IBrokerLoginForm.cs
```cs
   21: public interface IBrokerLoginForm
   23:     BrokerKind Broker { get; }
   24:     string DisplayName { get; }
   27:     bool CanSubmit { get; }
   34:     void ApplyToOptions();
   37:     string GetSessionAccountLabel();
   40:     string GetTimeoutErrorMessage();
   43:     string GetFailureMessage();
   46:     void Load();
   49:     void Save();
   53:     ConnectionState CurrentState { get; }
   57:     bool IsConnecting { get; }
   60:     string? ErrorMessage { get; }
```

## src/windows/Core/TradingTerminal.Core/Brokers/IBrokerLoginFormFactory.cs
```cs
    8: public interface IBrokerLoginFormFactory
   11:     IReadOnlyList<IBrokerLoginForm> All { get; }
   14:     IBrokerLoginForm Get(BrokerKind kind);
```

## src/windows/Core/TradingTerminal.Core/Brokers/IBrokerSelector.cs
```cs
   16: public interface IBrokerSelector
   19:     IReadOnlyList<BrokerKind> AvailableKinds { get; }
   22:     bool IsAvailable(BrokerKind kind);
   25:     IReadOnlyList<BrokerKind> Connected { get; }
   27:     bool IsConnected(BrokerKind kind);
   30:     IBrokerClient Get(BrokerKind kind);
   32:     BrokerConnectionMode ModeOf(BrokerKind kind);
   35:     IObservable<ConnectionState> StateOf(BrokerKind kind);
   38:     ConnectionState CurrentStateOf(BrokerKind kind);
   41:     event EventHandler<BrokerStateChangedEventArgs>? StateChanged;
   49:     Task ConnectAsync(BrokerKind kind, CancellationToken ct = default);
   52:     Task DisconnectAsync(BrokerKind kind, CancellationToken ct = default);
   55: public sealed record BrokerStateChangedEventArgs(BrokerKind Kind, ConnectionState State);
```

## src/windows/Core/TradingTerminal.Core/Brokers/Upstox/IUpstoxAuthService.cs
```cs
    9: public interface IUpstoxAuthService
   17:     string BuildAuthorizationUrl(string baseUrl, string apiKey, string redirectUri);
   24:     Task<string> ExchangeCodeForTokenAsync(
   25:     string baseUrl,
   26:     string apiKey,
   27:     string apiSecret,
   28:     string redirectUri,
   29:     string code,
   30:     CancellationToken ct = default);
```
