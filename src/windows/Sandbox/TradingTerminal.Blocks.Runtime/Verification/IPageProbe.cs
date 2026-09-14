using DaxAlgo.Blocks;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.Runtime.Verification;

/// <summary>What driving a unit with its real page showed.</summary>
/// <param name="Drive">The drive the unit went through while its page was open.</param>
/// <param name="Findings">The drive's findings plus the page's own (<c>page.threw</c>, <c>page.blank</c>).</param>
/// <param name="Png">The page as it looked before the unit stopped, or null.</param>
/// <param name="Width">The picture's width in pixels.</param>
/// <param name="Height">The picture's height in pixels.</param>
public sealed record PageCheck(DriveReport Drive, IReadOnlyList<DriveFinding> Findings, byte[]? Png, int Width, int Height)
{
    public bool Passed => Findings.All(f => f.Severity != DriveSeverity.Failure);
}

/// <summary>
/// Runs a unit against the synthetic market with the page it ships actually open.
///
/// <para>An interface here because the only implementation needs WPF and WebView2, and nothing that
/// gates a build may depend on either. A host without them passes none, and the gate drives the unit
/// with the recording stand-in instead — which still catches a unit that never talks to its page.</para>
/// </summary>
public interface IPageProbe
{
    /// <summary>False when this machine cannot open a page (no WebView2 runtime).</summary>
    bool IsAvailable { get; }

    Task<PageCheck> RunAsync(
        Func<IUnit> factory,
        IReadOnlyList<StrategyFile> pageFiles,
        string unitId,
        DriveOptions drive,
        CancellationToken ct = default);
}
