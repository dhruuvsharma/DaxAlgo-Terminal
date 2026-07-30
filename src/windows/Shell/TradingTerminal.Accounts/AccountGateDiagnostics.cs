using System.Diagnostics;

namespace TradingTerminal.Accounts;

public enum AccountGateDiagnosticCategory
{
    ProviderUnavailable,
    AttemptCancelled,
    SessionInvalid,
    EntitlementDenied,
    SignOutCancelled,
    UnexpectedProviderFailure,
}

public readonly record struct AccountGateDiagnosticSignal(
    AccountGateDiagnosticCategory Category,
    Guid CorrelationId);

public interface IAccountGateDiagnostics
{
    void Record(AccountGateDiagnosticSignal signal);
}

internal sealed class TraceAccountGateDiagnostics : IAccountGateDiagnostics
{
    public static TraceAccountGateDiagnostics Instance { get; } = new();

    public void Record(AccountGateDiagnosticSignal signal) =>
        Trace.TraceWarning(
            "AccountGate category={0} correlation={1:N}",
            signal.Category,
            signal.CorrelationId);
}

internal static class AccountGateDiagnosticsExtensions
{
    public static void RecordSafely(
        this IAccountGateDiagnostics diagnostics,
        AccountGateDiagnosticCategory category,
        Guid correlationId)
    {
        try
        {
            diagnostics.Record(new AccountGateDiagnosticSignal(category, correlationId));
        }
        catch
        {
            // Diagnostics must never turn an access denial into an application crash.
        }
    }
}
