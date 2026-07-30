using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Open-core seam for loading protected strategy artifacts. The public host knows only the artifact
/// path and the normal strategy-registration shape; the official installer supplies the engine that
/// verifies, decrypts, and executes the protected payload.
/// </summary>
public interface IProtectedStrategyEngine
{
    /// <summary>Loads every strategy registration described by a protected <c>.daxq</c> package.</summary>
    IReadOnlyList<ProtectedStrategyRegistration> LoadStrategies(string daxqPath);
}

/// <summary>
/// The three existing host seams a protected strategy contributes. Keeping the descriptor limited to
/// these public contracts lets protected and managed strategies flow through the same catalog,
/// backtest, and live-view factories without exposing the protected runtime to the public build.
/// </summary>
public sealed record ProtectedStrategyRegistration(
    ITradingStrategy Strategy,
    BacktestStrategyOption BacktestStrategy,
    StrategyFactoryRegistration StrategyFactory);
