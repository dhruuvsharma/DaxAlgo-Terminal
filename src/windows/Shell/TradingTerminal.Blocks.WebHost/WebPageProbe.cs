using DaxAlgo.Blocks;
using TradingTerminal.Blocks.Runtime.Verification;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Blocks.WebHost;

/// <summary>The page rung for a build gate: <see cref="PageProbe"/> behind <see cref="IPageProbe"/>.</summary>
public sealed class WebPageProbe(int width = 1280, int height = 800, string? workRoot = null) : IPageProbe
{
    public bool IsAvailable => WebUnitView.RuntimeAvailable;

    public async Task<PageCheck> RunAsync(
        Func<IUnit> factory,
        IReadOnlyList<StrategyFile> pageFiles,
        string unitId,
        DriveOptions drive,
        CancellationToken ct = default)
    {
        var report = await PageProbe.RunAsync(
            factory, pageFiles, unitId, new PageProbeOptions(width, height, drive, workRoot), ct).ConfigureAwait(false);

        return new PageCheck(report.Drive, report.Findings, report.Png, width, height);
    }
}
