using System.Globalization;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Pure helpers that turn a parameter grid into a list of fresh-strategy-builders for
/// walk-forward optimisation. Each builder takes the <see cref="Contract"/> and returns a
/// new <see cref="IBacktestStrategy"/> with no leaked state, so every window gets a clean
/// instance. Used by both the CLI's 'walkforward' subcommand and the WPF Backtest analysis
/// tab.
/// </summary>
public static class WalkForwardGridBuilders
{
    public static IReadOnlyList<(string Label, Func<Contract, IBacktestStrategy> Builder)> Build(
        string strategyId,
        int[] lookbacks,
        double[] entries,
        double[] stops,
        double[] trails,
        double[] thresholds,
        int[] holds,
        double[] entryZ,
        int quantity) => strategyId.ToLowerInvariant() switch
    {
        "meanreversion" or "mean-reversion" => MeanReversion(lookbacks, entries, stops, quantity),
        "donchianbreakout" or "donchian" or "breakout" => Donchian(lookbacks, trails, quantity),
        "microprice" => Microprice(thresholds, holds, quantity),
        "ornsteinuhlenbeck" or "ou" => OrnsteinUhlenbeck(lookbacks, entryZ, quantity),
        _ => throw new ArgumentException($"Walk-forward grid not defined for '{strategyId}'."),
    };

    public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> MeanReversion(
        int[] lookbacks, double[] entries, double[] stops, int qty)
    {
        var list = new List<(string, Func<Contract, IBacktestStrategy>)>();
        foreach (var l in lookbacks)
            foreach (var e in entries)
                foreach (var s in stops)
                {
                    int lc = l; double ec = e, sc = s;
                    list.Add(($"mr-lk{lc}-e{ec.ToString(CultureInfo.InvariantCulture)}-s{sc.ToString(CultureInfo.InvariantCulture)}",
                        c => new MeanReversionStrategy(c, lc, ec, sc, qty)));
                }
        return list;
    }

    public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> Donchian(
        int[] lookbacks, double[] trails, int qty)
    {
        var list = new List<(string, Func<Contract, IBacktestStrategy>)>();
        foreach (var l in lookbacks)
            foreach (var s in trails)
            {
                int lc = l; double sc = s;
                list.Add(($"don-lk{lc}-trail{sc.ToString(CultureInfo.InvariantCulture)}",
                    c => new DonchianBreakoutStrategy(c, lc, sc, qty)));
            }
        return list;
    }

    public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> Microprice(
        double[] thresholds, int[] holds, int qty)
    {
        var list = new List<(string, Func<Contract, IBacktestStrategy>)>();
        foreach (var t in thresholds)
            foreach (var h in holds)
            {
                double tc = t; int hc = h;
                list.Add(($"mp-t{tc.ToString(CultureInfo.InvariantCulture)}-h{hc}",
                    c => new MicropriceStrategy(c, tc, hc, qty)));
            }
        return list;
    }

    public static IReadOnlyList<(string, Func<Contract, IBacktestStrategy>)> OrnsteinUhlenbeck(
        int[] lookbacks, double[] entryZ, int qty)
    {
        var list = new List<(string, Func<Contract, IBacktestStrategy>)>();
        foreach (var l in lookbacks)
            foreach (var ez in entryZ)
            {
                int lc = l; double ezc = ez;
                list.Add(($"ou-lk{lc}-z{ezc.ToString(CultureInfo.InvariantCulture)}",
                    c => new OrnsteinUhlenbeckStrategy(c, lookback: lc, entryZ: ezc, quantity: qty)));
            }
        return list;
    }
}
