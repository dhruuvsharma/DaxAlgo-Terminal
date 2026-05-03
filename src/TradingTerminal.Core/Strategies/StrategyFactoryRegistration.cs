namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Pure-data record describing how to build the (view, view-model) pair for a strategy.
/// Strategy assemblies register one of these via DI in their <c>AddXxxStrategy()</c> extension —
/// that's the only seam between a strategy and the shell.
/// </summary>
public sealed record StrategyFactoryRegistration(
    string StrategyId,
    Func<IServiceProvider, object> ViewFactory,
    Func<IServiceProvider, object> ViewModelFactory);
