using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Research;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Turns a bridged <see cref="ReproSignalManifest"/> into runnable backtest strategy descriptors:
/// a <see cref="StrategyKernelDescriptor"/> (the portfolio-shaped replay kernel, for the Studio/CLI)
/// and a <see cref="BacktestStrategyOption"/> (the single-instrument catalog entry, for the Backtest
/// tab / <c>IBacktestStrategyRegistry</c>). Both carry the source paper URL as provenance so the
/// clickable paper pill survives, and both advertise <c>L1 | Bars</c> — signal replay needs no
/// tape/depth.
///
/// <para>Data/signals only: the produced strategies submit through the engine router, never a broker.
/// There is no live order path.</para>
/// </summary>
public static class ReproducedStrategyFactory
{
    /// <summary>Prefix for ids of reproduced strategies so they're recognisable in the catalog and can't
    /// collide with built-ins.</summary>
    public const string IdPrefix = "repro-";

    /// <summary>Stable strategy id for a manifest: <c>repro-{arxivId}@{commit8}</c> (commit truncated for
    /// readability). Same paper+commit → same id, so re-saving replaces rather than duplicates.</summary>
    public static string IdFor(ReproSignalManifest manifest)
    {
        var arxiv = string.IsNullOrWhiteSpace(manifest.Paper.ArxivId) ? "unknown" : manifest.Paper.ArxivId;
        var commit = manifest.RepoCommit.Length > 8 ? manifest.RepoCommit[..8] : manifest.RepoCommit;
        return $"{IdPrefix}{arxiv}@{commit}";
    }

    private static string DisplayNameFor(ReproSignalManifest manifest)
    {
        var title = string.IsNullOrWhiteSpace(manifest.Paper.Title) ? manifest.Paper.ArxivId : manifest.Paper.Title;
        return $"Reproduced: {title}";
    }

    /// <summary>Build the portfolio-shaped kernel descriptor for the Studio/CLI native path.</summary>
    public static StrategyKernelDescriptor ToKernelDescriptor(ReproSignalManifest manifest, long qty = 1) =>
        new(
            Id: IdFor(manifest),
            Name: DisplayNameFor(manifest),
            Description:
                $"Replay of paper {manifest.Paper.ArxivId} (commit {Short(manifest.RepoCommit)}, env {Short(manifest.EnvHash.Value)}); " +
                $"{manifest.Signals.Count} signal(s) over {manifest.Instruments.Count} instrument(s). Data: L1 | Bars.",
            Schema: Core.Backtesting.StrategyParameterSchema.Empty,
            Create: () => new ReproducedSignalStrategyKernel(manifest, qty))
        {
            ResearchPaperUrl = NullIfEmpty(manifest.Paper.Url),
        };

    /// <summary>
    /// Build the single-instrument catalog option for <c>IBacktestStrategyRegistry</c> (the Backtest tab
    /// / authoring registration path). It replays the manifest signals for whichever contract the run is
    /// built against (matched to a manifest instrument by the run universe).
    /// </summary>
    public static BacktestStrategyOption ToBacktestOption(ReproSignalManifest manifest, long qty = 1)
    {
        var id = IdFor(manifest);

        // The legacy option seam is single-instrument and contract-keyed; the run picks the instrument.
        // We map the built contract to the manifest's primary instrument (single-paper runs are the norm);
        // multi-instrument replay is the kernel descriptor's job.
        var primary = manifest.Instruments.FirstOrDefault();

        return new BacktestStrategyOption(
            Id: id,
            DisplayName: DisplayNameFor(manifest),
            Build: contract => new ReproducedSignalBacktestStrategy(contract, manifest, primary, qty))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars,
            ResearchPaperUrl = NullIfEmpty(manifest.Paper.Url),
        };
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 8 ? s[..8] : s);
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
