# TradingTerminal.Accounts — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Shell/TradingTerminal.Accounts/AccountGateCoordinator.cs
```cs
   23: public bool IsGranted => Failure == AccountGateAttemptFailure.None && Decision?.IsGranted == true;
   45: public Task<AccountGateAttempt> AcquireAccessAsync(CancellationToken ct = default) =>
   48: public Task<AccountGateAttempt> AcquireLocalDevelopmentAccessAsync(
  169: public async Task<AccountGateSignOutResult> SignOutAsync(CancellationToken ct = default)
  206: public static string ForDenial(EntitlementAccessDecision decision)
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateDiagnostics.cs
```cs
    5: public enum AccountGateDiagnosticCategory
   15: public readonly record struct AccountGateDiagnosticSignal(
   19: public interface IAccountGateDiagnostics
   21:     void Record(AccountGateDiagnosticSignal signal);
   26: public static TraceAccountGateDiagnostics Instance { get; } = new();
   28: public void Record(AccountGateDiagnosticSignal signal) =>
   37: public static void RecordSafely(
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateEditionProfile.cs
```cs
   11: public static AccountGateEditionProfile For(AppEdition edition) => edition switch
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateRunner.cs
```cs
    6: public static class AccountGateRunner
   16: public static bool Show(AppEdition requiredEdition)
   22: public static bool Show(
   40: public static bool ClearStoredAccount()
   46: public static bool Show(
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateServices.cs
```cs
   31: public static AccountGateServices Create(
  153: public static bool CanUseLocalAdapter(string environmentName, bool isDebugBuild) =>
  173: public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
  189: public async Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
  211: public Task SignOutAsync(CancellationToken ct = default)
  219: public string? GetIdToken(AccountSessionSnapshot session)
  229: public bool TryBindPlatformAccount(
  253: public AccountSessionSnapshot GetCanonicalSession(AccountSessionSnapshot session)
  273: public DevelopmentAccountAuthenticationService(TimeProvider timeProvider)
  282: public DevelopmentAccountAuthenticationService(
  294: public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
  318: public async Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
  339: public Task<AccountSessionSnapshot> AuthenticateLocallyAsync(
  353: public Task SignOutAsync(CancellationToken ct = default)
  365: public Task<SubscriptionEntitlement?> GetEntitlementAsync(
  383: public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
  389: public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
  395: public Task SignOutAsync(CancellationToken ct = default)
  404: public Task<SubscriptionEntitlement?> GetEntitlementAsync(
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateViewModel.cs
```cs
   15: public AccountGateViewModel(
   53: public string PlanName { get; }
   55: public string PlanPrice { get; }
   57: public string PlanSummary { get; }
   59: public string EnvironmentNotice { get; }
   61: public bool HasEnvironmentNotice { get; }
   63: public bool HasLocalDeveloperAccess { get; }
   89: public event Action<bool>? Completed;
  211: public void Dispose()
```

## src/windows/Shell/TradingTerminal.Accounts/AccountGateWindow.xaml.cs
```cs
    5: public partial class AccountGateWindow : MetroWindow
```

## src/windows/Shell/TradingTerminal.Accounts/DevelopmentAccountSessionStore.cs
```cs
   36: public static DevelopmentAccountSessionStore CreateDefault()
   46: public AccountSessionSnapshot? Load()
   86: public bool Save(AccountSessionSnapshot session)
  136: public bool Clear()
  172: public static DpapiAccountSessionProtector Instance { get; } = new();
  174: public byte[] Protect(byte[] plaintext) =>
  180: public byte[] Unprotect(byte[] ciphertext) =>
```

## src/windows/Shell/TradingTerminal.Accounts/GoogleOAuthClient.cs
```cs
   19: public override string ToString() => "GoogleIdentity { IdToken = [REDACTED] }";
   69: public async Task<GoogleIdentity> AuthenticateAsync(CancellationToken ct = default)
  455: public static SystemGoogleOAuthBrowser Instance { get; } = new();
  457: public void Open(Uri authorizationUri) =>
  467: public static LoopbackGoogleOAuthCallbackReceiverFactory Instance { get; } = new();
  469: public IGoogleOAuthCallbackReceiver Create() =>
  486: public LoopbackGoogleOAuthCallbackReceiver(int port)
  493: public Uri RedirectUri { get; }
  495: public async Task<GoogleOAuthCallback> WaitForCallbackAsync(CancellationToken ct)
  537: public ValueTask DisposeAsync()
```

## src/windows/Shell/TradingTerminal.Accounts/PlatformEntitlementService.cs
```cs
   55: public async Task<SubscriptionEntitlement?> GetEntitlementAsync(
  209: public Task<OfflineLeaseValidationResult> ValidateAsync(
  374: public static PersistedDeviceIdentityProvider CreateDefault()
  383: public Guid GetDeviceId()
  476: public static bool TryCreateBaseUri(string? configuredValue, out Uri? baseUri)
```
