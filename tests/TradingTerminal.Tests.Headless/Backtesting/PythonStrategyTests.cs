using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Polyglot;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>End-to-end check that a Python-authored strategy (daxalgo_bt example) runs on the new
/// engine via <see cref="PythonStrategyKernel"/>. Soft-skips when no Python interpreter is on PATH so
/// CI without Python stays green — mirroring the Postgres-tests-self-skip convention.</summary>
public sealed class PythonStrategyTests
{
    private static readonly InstrumentId Id = new(1);

    private static string? FindPython()
    {
        foreach (var exe in new[] { "python", "python3" })
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("--version");
                using var p = Process.Start(psi);
                if (p is null) continue;
                if (p.WaitForExit(5000) && p.ExitCode == 0) return exe;
            }
            catch { /* not on PATH */ }
        }
        return null;
    }

    private static string ExampleScript([CallerFilePath] string? thisFile = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "..",
            "tools", "py-backtester", "examples", "mean_reversion.py"));

    [Fact]
    public async Task Python_strategy_runs_on_the_new_engine()
    {
        var python = FindPython();
        if (python is null) return; // soft-skip: no Python available
        var script = ExampleScript();
        if (!File.Exists(script)) return;

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = Enumerable.Range(0, 800).Select(i =>
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0);
            return MarketEvent.OfQuote(Id, new Tick(start.AddSeconds(i), mid - 0.01, mid + 0.01, 10, 10));
        });

        var spec = new RunSpec(
            Universe.Single(new InstrumentSpec(Id, Contract.UsStock("TEST"), 0.01, 1.0)),
            new DataSpec(),
            StrategyId: "py:mean_reversion",
            Parameters: new StrategyParameters(new Dictionary<string, double>
            {
                ["lookback"] = 20, ["entryZ"] = 1.0, ["exitZ"] = 0.2, ["qty"] = 5,
            }));

        var report = await new BacktestEngine(new InMemoryMarketDataFeed(events))
            .RunAsync(spec, new PythonStrategyKernel(python, script));

        report.Summary.EventsProcessed.Should().Be(800);
        report.Trades.Should().NotBeEmpty("the Python mean-reversion example should trade on an oscillating series");
    }
}
