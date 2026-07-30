using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

internal enum AccountGateAttemptFailure
{
    None,
    AttemptCancelled,
    SessionInvalid,
    ProviderUnavailable,
    EntitlementDenied,
    UnexpectedProviderFailure,
}

internal sealed record AccountGateAttempt(
    AccountSessionSnapshot? Session,
    EntitlementAccessDecision? Decision,
    AccountGateAttemptFailure Failure,
    string? FailureMessage,
    Guid CorrelationId)
{
    public bool IsGranted => Failure == AccountGateAttemptFailure.None && Decision?.IsGranted == true;
}

internal sealed record AccountGateSignOutResult(
    bool IsSuccessful,
    bool IsCancelled,
    string? FailureMessage,
    Guid CorrelationId);

internal sealed class AccountProviderUnavailableException(string message) : InvalidOperationException(message);

internal sealed class AccountGateCoordinator(
    IAccountAuthenticationService authentication,
    IEntitlementService entitlements,
    AppEdition requiredEdition,
    TimeProvider timeProvider,
    IAccountGateDiagnostics? diagnostics = null)
{
    private readonly IAccountGateDiagnostics _diagnostics =
        diagnostics ?? TraceAccountGateDiagnostics.Instance;
    private int _forceInteractiveAuthentication;

    public Task<AccountGateAttempt> AcquireAccessAsync(CancellationToken ct = default) =>
        AcquireAccessAsync(useLocalDevelopmentAccount: false, ct);

    public Task<AccountGateAttempt> AcquireLocalDevelopmentAccessAsync(
        CancellationToken ct = default) =>
        AcquireAccessAsync(useLocalDevelopmentAccount: true, ct);

    private async Task<AccountGateAttempt> AcquireAccessAsync(
        bool useLocalDevelopmentAccount,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid();
        try
        {
            var now = timeProvider.GetUtcNow();
            var forceInteractive = Volatile.Read(ref _forceInteractiveAuthentication) != 0;
            AccountSessionSnapshot? session;
            if (useLocalDevelopmentAccount)
            {
                if (authentication is not IDevelopmentAccountAuthenticationService development)
                {
                    throw new AccountProviderUnavailableException(
                        "Local developer access is not available in this build.");
                }

                session = await development.AuthenticateLocallyAsync(ct);
                Volatile.Write(ref _forceInteractiveAuthentication, 0);
            }
            else
            {
                session = forceInteractive
                    ? null
                    : await authentication.GetCurrentSessionAsync(ct);
                if (forceInteractive || session is null || !session.IsActiveAt(now))
                {
                    session = await authentication.AuthenticateAsync(ct);
                    Volatile.Write(ref _forceInteractiveAuthentication, 0);
                }
            }

            now = timeProvider.GetUtcNow();
            if (!session.IsActiveAt(now))
            {
                _diagnostics.RecordSafely(
                    AccountGateDiagnosticCategory.SessionInvalid,
                    correlationId);
                return new AccountGateAttempt(
                    session,
                    null,
                    AccountGateAttemptFailure.SessionInvalid,
                    "The account session is no longer valid. Sign in again and retry.",
                    correlationId);
            }

            var entitlement = await entitlements.GetEntitlementAsync(session, ct);
            if (authentication is ICanonicalAccountSessionSource canonicalSessionSource)
            {
                session = canonicalSessionSource.GetCanonicalSession(session);
            }

            var request = new EntitlementAccessRequest(
                session.Account.AccountId,
                requiredEdition,
                now);
            var decision = EntitlementAccessEvaluator.Evaluate(request, entitlement);

            if (decision.IsGranted)
            {
                return new AccountGateAttempt(
                    session,
                    decision,
                    AccountGateAttemptFailure.None,
                    null,
                    correlationId);
            }

            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.EntitlementDenied,
                correlationId);
            return new AccountGateAttempt(
                session,
                decision,
                AccountGateAttemptFailure.EntitlementDenied,
                AccountGateMessageFormatter.ForDenial(decision),
                correlationId);
        }
        catch (AccountProviderUnavailableException ex)
        {
            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.ProviderUnavailable,
                correlationId);
            return new AccountGateAttempt(
                null,
                null,
                AccountGateAttemptFailure.ProviderUnavailable,
                ex.Message,
                correlationId);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.AttemptCancelled,
                correlationId);
            return new AccountGateAttempt(
                null,
                null,
                AccountGateAttemptFailure.AttemptCancelled,
                "Account verification was cancelled. Retry when you are ready.",
                correlationId);
        }
        catch (Exception)
        {
            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.UnexpectedProviderFailure,
                correlationId);
            return new AccountGateAttempt(
                null,
                null,
                AccountGateAttemptFailure.UnexpectedProviderFailure,
                "Account access could not be verified. Check your connection and retry.",
                correlationId);
        }
    }

    public async Task<AccountGateSignOutResult> SignOutAsync(CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid();
        Volatile.Write(ref _forceInteractiveAuthentication, 1);

        try
        {
            await authentication.SignOutAsync(ct);
            return new AccountGateSignOutResult(true, false, null, correlationId);
        }
        catch (OperationCanceledException)
        {
            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.SignOutCancelled,
                correlationId);
            return new AccountGateSignOutResult(
                false,
                true,
                "Account switching was cancelled.",
                correlationId);
        }
        catch (Exception)
        {
            _diagnostics.RecordSafely(
                AccountGateDiagnosticCategory.UnexpectedProviderFailure,
                correlationId);
            return new AccountGateSignOutResult(
                false,
                false,
                "The current account could not be signed out. Retry or cancel.",
                correlationId);
        }
    }
}

internal static class AccountGateMessageFormatter
{
    public static string ForDenial(EntitlementAccessDecision decision)
    {
        var required = AccountGateEditionProfile.For(decision.RequiredEdition);
        return decision.Reason switch
        {
            EntitlementAccessReason.EditionInsufficient =>
                $"This account has the {PlanName(decision.GrantedEdition)} plan. " +
                $"Upgrade to {required.PlanName} ({required.Price}), then retry.",
            EntitlementAccessReason.EntitlementMissing =>
                "No active product plan was found for this account. Choose a plan in the marketplace, then retry.",
            EntitlementAccessReason.EntitlementNotYetValid =>
                "This plan is not active yet. Check the activation date, then retry.",
            EntitlementAccessReason.EntitlementExpired =>
                "This plan has expired. Renew it in the marketplace, then retry.",
            EntitlementAccessReason.EntitlementSuspended =>
                "This plan is suspended. Resolve the account issue in the marketplace, then retry.",
            EntitlementAccessReason.EntitlementRevoked =>
                "This plan is no longer valid. Contact support or select another plan, then retry.",
            EntitlementAccessReason.AccountMismatch =>
                "The entitlement belongs to a different account. Sign in with the purchasing account and retry.",
            _ => "This account cannot access this edition. Review the plan in the marketplace, then retry.",
        };
    }

    private static string PlanName(AppEdition? edition) => edition is { } value
        ? AccountGateEditionProfile.For(value).PlanName
        : "current";
}
