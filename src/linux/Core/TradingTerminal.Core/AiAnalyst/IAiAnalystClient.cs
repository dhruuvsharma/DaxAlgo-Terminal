namespace TradingTerminal.Core.AiAnalyst;

/// <summary>
/// The Core-side seam to the AI Market Analyst. One implementation calls the Python
/// sidecar over HTTP; the Null implementation always returns an "unavailable" report so
/// the UI and the notification enricher both degrade gracefully when the sidecar is
/// missing or unreachable.
///
/// Implementations MUST NOT throw — failures and timeouts surface as
/// <see cref="AnalystReport.Unavailable"/>.
/// </summary>
public interface IAiAnalystClient
{
    /// <summary>True when this client expects the sidecar to be reachable. The Null
    /// implementation returns false; the Http implementation returns true and lets the
    /// per-call timeout decide success/failure.</summary>
    bool IsAvailable { get; }

    Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default);
}
