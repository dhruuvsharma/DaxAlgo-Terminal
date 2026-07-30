# TradingTerminal.Core / Accounts — public API surface

Generated from the current source tree. Declaration lines only; multi-line signatures show their first line;
note: `[ObservableProperty]` private fields generate public properties that are NOT listed here.
Use: grep this file for a symbol, then open the cited file:line. Regenerate: gen-context.sh.

## src/windows/Core/TradingTerminal.Core/Accounts/AccountContractGuards.cs
```cs
    7: public static string NormalizeRequired(string value, string parameterName)
   13: public static string RequireOpaque(string value, string parameterName)
   19: public static string? NormalizeOptional(string? value) =>
   22: public static DateTimeOffset AsUtc(DateTimeOffset value) => value.ToUniversalTime();
   24: public static void ValidateEdition(AppEdition edition, string parameterName)
```

## src/windows/Core/TradingTerminal.Core/Accounts/AccountIdentity.cs
```cs
    8: public sealed record AccountIdentity
   10: public AccountIdentity(string accountId, string? displayName = null, string? emailAddress = null)
   17: public string AccountId { get; }
   19: public string? DisplayName { get; }
   21: public string? EmailAddress { get; }
   28: public sealed record AccountSessionSnapshot
   30: public AccountSessionSnapshot(
   57: public string SessionId { get; }
   59: public AccountIdentity Account { get; }
   61: public DateTimeOffset AuthenticatedAtUtc { get; }
   64: public DateTimeOffset? ExpiresAtUtc { get; }
   70: public bool IsActiveAt(DateTimeOffset currentUtc)
```

## src/windows/Core/TradingTerminal.Core/Accounts/AccountServices.cs
```cs
    7: public interface IAccountAuthenticationService
    9:     Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default);
   11:     Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default);
   13:     Task SignOutAsync(CancellationToken ct = default);
   17: public interface IEntitlementService
   19:     Task<SubscriptionEntitlement?> GetEntitlementAsync(
   20:     AccountSessionSnapshot session,
   21:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Accounts/EntitlementAccessEvaluator.cs
```cs
    6: public enum EntitlementAccessReason
   24: public sealed record EntitlementAccessRequest
   26: public EntitlementAccessRequest(
   37: public string AccountId { get; }
   39: public AppEdition RequiredEdition { get; }
   41: public DateTimeOffset CurrentUtc { get; }
   45: public sealed record EntitlementAccessDecision
   60: public bool IsGranted { get; }
   66: public AppEdition? GrantedEdition { get; }
   68: public EntitlementAccessReason Reason { get; }
   70: public AppEdition RequiredEdition { get; }
   72: public DateTimeOffset EvaluatedAtUtc { get; }
   79: public static class EntitlementAccessEvaluator
   81: public static EntitlementAccessDecision Evaluate(
  137: public static EntitlementAccessDecision EvaluateOffline(
```

## src/windows/Core/TradingTerminal.Core/Accounts/EntitlementLeaseWireDto.cs
```cs
   11: public sealed record EntitlementLeaseWireDto
   13: public const int CurrentSchemaVersion = 1;
   15: public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(7);
   17: public EntitlementLeaseWireDto(
   88: public int SchemaVersion { get; }
   91: public string LeaseId { get; }
   95: public SubscriptionEntitlementState State { get; }
   99: public AppEdition Edition { get; }
  102: public string ProductAccountId { get; }
  105: public Guid DeviceId { get; }
  108: public string Issuer { get; }
  111: public string Audience { get; }
  114: public DateTimeOffset IssuedAtUtc { get; }
  117: public DateTimeOffset NotBeforeUtc { get; }
  120: public DateTimeOffset ExpiresAtUtc { get; }
  123: public string KeyId { get; }
  126: public string Algorithm { get; }
  129: public string EncodedPayload { get; }
  132: public string EncodedSignature { get; }
  134: public bool IsBoundTo(string productAccountId, Guid deviceId) =>
  141: public SignedOfflineLeaseEnvelope ToSignedEnvelope() =>
```

## src/windows/Core/TradingTerminal.Core/Accounts/OfflineEntitlementLease.cs
```cs
    7: public sealed record OfflineEntitlementLease
    9: public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(7);
   11: public OfflineEntitlementLease(
   48: public string LeaseId { get; }
   50: public SubscriptionEntitlement Entitlement { get; }
   53: public string DeviceId { get; }
   56: public DateTimeOffset IssuedAtUtc { get; }
   59: public DateTimeOffset NotBeforeUtc { get; }
   62: public DateTimeOffset ExpiresAtUtc { get; }
   69: public sealed record SignedOfflineLeaseEnvelope
   71: public SignedOfflineLeaseEnvelope(
   83: public string EncodedPayload { get; }
   85: public string EncodedSignature { get; }
   87: public string KeyId { get; }
   89: public string Algorithm { get; }
   93: public enum OfflineLeaseValidationFailure
  105: public sealed record OfflineLeaseValidationResult
  115: public bool IsValid => Failure == OfflineLeaseValidationFailure.None && Lease is not null;
  117: public OfflineEntitlementLease? Lease { get; }
  119: public OfflineLeaseValidationFailure Failure { get; }
  121: public static OfflineLeaseValidationResult Valid(OfflineEntitlementLease lease)
  127: public static OfflineLeaseValidationResult Invalid(OfflineLeaseValidationFailure failure)
  147: public interface IOfflineLeaseValidator
  149:     Task<OfflineLeaseValidationResult> ValidateAsync(
  150:     SignedOfflineLeaseEnvelope envelope,
  151:     CancellationToken ct = default);
```

## src/windows/Core/TradingTerminal.Core/Accounts/SubscriptionEntitlement.cs
```cs
    9: public enum SubscriptionEntitlementState
   20: public sealed record SubscriptionEntitlement
   22: public SubscriptionEntitlement(
   76: public string AccountId { get; }
   78: public AppEdition Edition { get; }
   80: public SubscriptionEntitlementState State { get; }
   83: public DateTimeOffset ValidFromUtc { get; }
   86: public DateTimeOffset? ExpiresAtUtc { get; }
   89: public DateTimeOffset? GraceEndsAtUtc { get; }
   95: public string? SubscriptionReference { get; }
```
