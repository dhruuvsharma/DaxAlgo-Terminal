using TradingTerminal.Sandbox;

namespace TradingTerminal.Sandbox.Runtime;

/// <summary>
/// One committed model-portfolio sample taken after a historical bar has been processed.
/// </summary>
public readonly record struct SandboxBacktestPoint(
    DateTime TimestampUtc,
    SandboxPortfolioSnapshot Snapshot)
{
    /// <summary>The sampled equity, exposed directly for curve consumers.</summary>
    public double Equity => Snapshot.Equity;
}

/// <summary>
/// Deterministic, read-only output from one sandbox backtest run.
/// </summary>
public sealed record SandboxBacktestResult
{
    public SandboxBacktestResult(
        IEnumerable<SandboxBacktestPoint> equityCurve,
        SandboxPortfolioSnapshot finalSnapshot,
        IEnumerable<AlertRecord> alerts)
    {
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(alerts);

        EquityCurve = Array.AsReadOnly(equityCurve.ToArray());
        FinalSnapshot = finalSnapshot;
        Alerts = Array.AsReadOnly(alerts.ToArray());
    }

    /// <summary>Ordered committed samples, one per input bar.</summary>
    public IReadOnlyList<SandboxBacktestPoint> EquityCurve { get; }

    /// <summary>The completed account state after final liquidation, when required.</summary>
    public SandboxPortfolioSnapshot FinalSnapshot { get; }

    /// <summary>Accepted, host-mediated alerts in deterministic delivery order.</summary>
    public IReadOnlyList<AlertRecord> Alerts { get; }

    public bool Equals(SandboxBacktestResult? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        FinalSnapshot.Equals(other.FinalSnapshot) &&
        EquityCurve.SequenceEqual(other.EquityCurve) &&
        Alerts.SequenceEqual(other.Alerts);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FinalSnapshot);
        foreach (var point in EquityCurve)
            hash.Add(point);
        foreach (var alert in Alerts)
            hash.Add(alert);
        return hash.ToHashCode();
    }
}
