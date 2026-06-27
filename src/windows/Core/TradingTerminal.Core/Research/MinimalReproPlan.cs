namespace TradingTerminal.Core.Research;

/// <summary>
/// The full result of resolving a repo to a runnable minimal reproduction: the
/// <see cref="EnvResolutionPlan"/> (image + setup + entrypoint + data deps + env hash) plus whether the
/// sidecar could resolve it and a reason when it couldn't.
///
/// <para>Like every Research seam, failures fold into this record (<see cref="Empty"/>) rather than
/// throwing across the boundary. The plan is static-analysis output from the sidecar — no repo code is
/// ever executed to produce it. Records, SDK-free.</para>
/// </summary>
public sealed record MinimalReproPlan(
    bool Resolved,
    EnvResolutionPlan? Plan,
    string? Error)
{
    /// <summary>An empty/failed plan carrying the reason.</summary>
    public static MinimalReproPlan Empty(string reason) =>
        new(Resolved: false, Plan: null, Error: reason);

    /// <summary>A resolved plan.</summary>
    public static MinimalReproPlan Ok(EnvResolutionPlan plan) =>
        new(Resolved: true, Plan: plan, Error: null);
}
