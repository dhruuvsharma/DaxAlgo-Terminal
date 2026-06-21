using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// Bridges the 12 legacy engine strategies (the <see cref="IBacktestStrategyRegistry"/> catalog) into
/// the new <see cref="StrategyKernelDescriptor"/> catalog via <see cref="BacktestStrategyKernelAdapter"/>,
/// so they appear in the Studio and run on the new engine without being rewritten. They run with their
/// default parameters for now — exposing each strategy's legacy tunables through the new schema is a
/// later refinement; ids already provided by a native kernel are skipped to avoid collisions.
/// </summary>
public static class LegacyKernelDescriptors
{
    public static IEnumerable<StrategyKernelDescriptor> From(IBacktestStrategyRegistry registry, ISet<string> excludeIds)
    {
        foreach (var option in registry.All)
        {
            if (excludeIds.Contains(option.Id)) continue;
            var captured = option;
            yield return new StrategyKernelDescriptor(
                Id: captured.Id,
                Name: captured.DisplayName,
                Description: $"Engine strategy (default parameters). Data: {captured.DataRequirement}.",
                Schema: StrategyParameterSchema.Empty,
                Create: () => new BacktestStrategyKernelAdapter(contract => captured.Create(contract, null)));
        }
    }
}
