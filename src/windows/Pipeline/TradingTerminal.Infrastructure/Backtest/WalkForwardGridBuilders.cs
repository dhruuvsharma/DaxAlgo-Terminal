using System;
using System.Collections.Generic;
using System.Linq;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Resolves a strategy's walk-forward candidate grid. The grids themselves now live ON each strategy
/// option (<see cref="BacktestStrategyOption.WalkForwardGrid"/>) — declared in the catalog entry for
/// built-ins or in a plugin's registration — so this no longer hardcodes any strategy or references
/// any engine type. It is the single resolution point both the CLI 'walkforward' subcommand and the
/// WPF Backtest-analysis tab call. Each candidate's builder makes a FRESH strategy per window
/// (strategies are stateful), so no instance leaks across train→test→next-window.
/// </summary>
public static class WalkForwardGridBuilders
{
    /// <summary>The strategy's walk-forward grid for the given axes, as (label, fresh-builder) pairs.
    /// Throws when the option declares no grid.</summary>
    public static IReadOnlyList<(string Label, Func<Contract, IBacktestStrategy> Builder)> For(
        BacktestStrategyOption option, WalkForwardAxes axes)
    {
        var candidates = option.WalkForwardGrid?.Invoke(axes)
            ?? throw new ArgumentException($"Walk-forward grid not defined for '{option.Id}'.");
        return candidates.Select(c => (c.Label, c.Build)).ToList();
    }
}
