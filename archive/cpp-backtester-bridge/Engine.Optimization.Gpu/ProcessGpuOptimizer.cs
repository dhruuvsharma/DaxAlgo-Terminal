using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization.Gpu;

/// <summary>
/// C# bridge to the CUDA <c>gpu_optimizer</c> (tools/cpp-backtester/gpu/). Spawns it as a one-shot
/// subprocess, streams the bid/ask series + the lookback/entryZ grid in on stdin (the simple
/// whitespace protocol the .cu parses), and reads back one net-profit/trade line per combo. The GPU
/// mirrors the managed fill arithmetic, so net profit matches the CPU <see cref="GridOptimizer"/> —
/// run both on the same inputs to verify after you build the binary.
///
/// GPU support is deliberately narrow: only the <c>meanReversion</c> kernel, only the
/// <see cref="OptimizationCriterion.NetProfit"/> criterion, and only sweeps over lookback / entryZ.
/// Anything else throws <see cref="GpuUnavailableException"/> so the caller falls back to CPU.
/// </summary>
public sealed class ProcessGpuOptimizer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _exePath;

    public ProcessGpuOptimizer(string exePath) => _exePath = exePath;

    public bool IsAvailable => File.Exists(_exePath);

    public static bool Supports(OptimizationSpec spec) =>
        spec.BaseRun.StrategyId == "meanReversion"
        && spec.Criterion == OptimizationCriterion.NetProfit
        && spec.Axes.Count > 0
        && spec.Axes.All(a => a.Name is "lookback" or "entryZ");

    public async Task<OptimizationResult> RunAsync(
        OptimizationSpec spec, IReadOnlyList<(double Bid, double Ask)> quotes, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new GpuUnavailableException($"GPU optimizer not found at {_exePath}.");
        if (!Supports(spec)) throw new GpuUnavailableException("Spec is outside the GPU optimizer's supported set.");
        if (quotes.Count == 0) throw new GpuUnavailableException("No quotes to evaluate.");

        var baseParams = spec.BaseRun.ParametersOrEmpty;
        var lookbacks = ValuesFor("lookback", spec, baseParams.GetOr("lookback", 50)).Select(v => (int)Math.Round(v)).ToList();
        var entryZs = ValuesFor("entryZ", spec, baseParams.GetOr("entryZ", 2.0));
        var exitZ = baseParams.GetOr("exitZ", 0.5);
        var qty = baseParams.GetInt("qty", 1);

        var stdout = await RunProcessAsync(BuildInput(quotes, lookbacks, entryZs, exitZ, qty), ct).ConfigureAwait(false);
        var trials = ParseTrials(stdout);

        var ranked = trials.OrderByDescending(t => t.Score).ToList();
        return new OptimizationResult(spec.Criterion, ranked, ranked.FirstOrDefault());
    }

    private static IReadOnlyList<double> ValuesFor(string name, OptimizationSpec spec, double fallback)
    {
        var axis = spec.Axes.FirstOrDefault(a => a.Name == name);
        return axis?.Values ?? new[] { fallback };
    }

    private static string BuildInput(
        IReadOnlyList<(double Bid, double Ask)> quotes, IReadOnlyList<int> lookbacks, IReadOnlyList<double> entryZs, double exitZ, long qty)
    {
        var sb = new StringBuilder();
        sb.Append("N ").Append(quotes.Count.ToString(Inv));
        foreach (var (bid, ask) in quotes)
            sb.Append(' ').Append(bid.ToString("R", Inv)).Append(' ').Append(ask.ToString("R", Inv));
        sb.Append("\nQTY ").Append(qty.ToString(Inv));
        sb.Append("\nEXITZ ").Append(exitZ.ToString("R", Inv));
        sb.Append("\nLOOKBACKS ").Append(lookbacks.Count.ToString(Inv));
        foreach (var l in lookbacks) sb.Append(' ').Append(l.ToString(Inv));
        sb.Append("\nENTRYZS ").Append(entryZs.Count.ToString(Inv));
        foreach (var e in entryZs) sb.Append(' ').Append(e.ToString("R", Inv));
        sb.Append("\nEND\n");
        return sb.ToString();
    }

    private List<OptimizationTrial> ParseTrials(string stdout)
    {
        var trials = new List<OptimizationTrial>();
        foreach (var line in stdout.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, Inv, out var lookback)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, Inv, out var entryZ)) continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, Inv, out var net)) continue;
            if (!int.TryParse(parts[3], NumberStyles.Integer, Inv, out var trades)) continue;

            var p = new Dictionary<string, double> { ["lookback"] = lookback, ["entryZ"] = entryZ };
            trials.Add(new OptimizationTrial(p, Score: net, NetProfit: net, TradeCount: trades));
        }
        if (trials.Count == 0)
            throw new GpuUnavailableException($"GPU optimizer produced no parseable output. Raw:\n{stdout}");
        return trials;
    }

    private async Task<string> RunProcessAsync(string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new GpuUnavailableException($"Failed to launch {_exePath}.");

        _ = Task.Run(async () => { await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false); }, ct);

        await proc.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new GpuUnavailableException($"gpu_optimizer exited with code {proc.ExitCode}.");
        return stdout;
    }
}
