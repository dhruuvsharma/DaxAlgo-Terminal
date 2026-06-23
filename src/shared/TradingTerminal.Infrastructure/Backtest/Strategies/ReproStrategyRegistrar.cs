using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Default <see cref="IReproStrategyRegistrar"/>: runs the bridge, scores confidence, builds the
/// paper-tagged option via <see cref="ReproducedStrategyFactory"/>, and registers it into the live
/// <see cref="IBacktestStrategyRegistry"/>. Never throws across the seam.
/// </summary>
internal sealed class ReproStrategyRegistrar : IReproStrategyRegistrar
{
    private readonly IReproSignalBridge _bridge;
    private readonly IReplicationConfidenceScorer _scorer;
    private readonly IBacktestStrategyRegistry _registry;
    private readonly ILogger<ReproStrategyRegistrar> _logger;

    public ReproStrategyRegistrar(
        IReproSignalBridge bridge,
        IReplicationConfidenceScorer scorer,
        IBacktestStrategyRegistry registry,
        ILogger<ReproStrategyRegistrar> logger)
    {
        _bridge = bridge;
        _scorer = scorer;
        _registry = registry;
        _logger = logger;
    }

    public async Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default)
    {
        try
        {
            var manifest = await _bridge.MapAsync(result, ct).ConfigureAwait(false);
            var confidence = _scorer.Score(result, manifest);

            if (!manifest.HasSignals)
                return ReproRegistration.Failed(
                    "Reproduction produced no usable signals — nothing to register.", manifest);

            var option = ReproducedStrategyFactory.ToBacktestOption(manifest);
            _registry.Register(option);
            _logger.LogInformation(
                "Registered reproduced strategy {Id} (paper {Arxiv}@{Commit}, confidence {Score:0.00})",
                option.Id, manifest.Paper.ArxivId, manifest.RepoCommit, confidence.Score);

            return new ReproRegistration(true, option, manifest, confidence, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register reproduced strategy for {Arxiv}", result.PaperArxivId);
            return ReproRegistration.Failed($"Registration failed: {ex.Message}");
        }
    }
}
