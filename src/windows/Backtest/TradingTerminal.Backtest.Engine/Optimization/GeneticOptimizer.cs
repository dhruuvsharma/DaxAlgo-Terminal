using System.Collections.Concurrent;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Optimization;

/// <summary>
/// Genetic parameter search for spaces too large to grid exhaustively. A genome is one value per
/// axis (drawn from that axis's allowed values); fitness is the criterion score. Each generation
/// evaluates the population in parallel (de-duplicated through a cache so revisited genomes aren't
/// re-run), keeps the elites, and breeds the rest by tournament selection + uniform crossover +
/// mutation. Deterministic for a fixed <see cref="GeneticOptions.Seed"/>. Same factory contract as
/// <see cref="GridOptimizer"/>.
/// </summary>
public sealed class GeneticOptimizer
{
    private readonly Func<IMarketDataFeed> _feedFactory;
    private readonly Func<IStrategyKernel> _kernelFactory;

    public GeneticOptimizer(Func<IMarketDataFeed> feedFactory, Func<IStrategyKernel> kernelFactory)
    {
        _feedFactory = feedFactory;
        _kernelFactory = kernelFactory;
    }

    public async Task<OptimizationResult> RunAsync(
        OptimizationSpec spec, GeneticOptions options, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var axes = spec.Axes;
        if (axes.Count == 0)
            return new OptimizationResult(spec.Criterion, Array.Empty<OptimizationTrial>(), null);

        var rng = new Random(options.Seed);
        var cache = new ConcurrentDictionary<string, OptimizationTrial>();
        var evaluated = 0;

        var population = new List<Dictionary<string, double>>(options.PopulationSize);
        for (var i = 0; i < options.PopulationSize; i++) population.Add(RandomGenome(axes, rng));

        for (var gen = 0; gen < options.Generations; gen++)
        {
            // Evaluate everything not already cached, in parallel and de-duplicated.
            var pending = population
                .GroupBy(Key)
                .Where(g => !cache.ContainsKey(g.Key))
                .Select(g => g.First())
                .ToList();

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                async (genome, token) =>
                {
                    var trial = await TrialRunner
                        .EvaluateAsync(_feedFactory, _kernelFactory, spec.BaseRun, spec.Criterion, genome, token)
                        .ConfigureAwait(false);
                    cache[Key(genome)] = trial;
                    progress?.Report(Interlocked.Increment(ref evaluated));
                }).ConfigureAwait(false);

            if (gen == options.Generations - 1) break;

            var scored = population
                .Select(g => (Genome: g, Score: cache[Key(g)].Score))
                .OrderByDescending(p => p.Score)
                .ToList();

            var next = new List<Dictionary<string, double>>(options.PopulationSize);
            foreach (var elite in scored.Take(Math.Min(options.Elites, scored.Count)))
                next.Add(new Dictionary<string, double>(elite.Genome));

            while (next.Count < options.PopulationSize)
            {
                var a = Tournament(scored, options.TournamentSize, rng);
                var b = Tournament(scored, options.TournamentSize, rng);
                var child = Crossover(a, b, axes, rng);
                Mutate(child, axes, options.MutationRate, rng);
                next.Add(child);
            }
            population = next;
        }

        var ranked = cache.Values.OrderByDescending(t => t.Score).ToList();
        return new OptimizationResult(spec.Criterion, ranked, ranked.FirstOrDefault());
    }

    private static Dictionary<string, double> RandomGenome(IReadOnlyList<ParameterAxis> axes, Random rng)
    {
        var g = new Dictionary<string, double>(axes.Count);
        foreach (var axis in axes) g[axis.Name] = axis.Values[rng.Next(axis.Values.Count)];
        return g;
    }

    private static Dictionary<string, double> Crossover(
        Dictionary<string, double> a, Dictionary<string, double> b, IReadOnlyList<ParameterAxis> axes, Random rng)
    {
        var child = new Dictionary<string, double>(axes.Count);
        foreach (var axis in axes) child[axis.Name] = rng.NextDouble() < 0.5 ? a[axis.Name] : b[axis.Name];
        return child;
    }

    private static void Mutate(Dictionary<string, double> genome, IReadOnlyList<ParameterAxis> axes, double rate, Random rng)
    {
        foreach (var axis in axes)
            if (rng.NextDouble() < rate)
                genome[axis.Name] = axis.Values[rng.Next(axis.Values.Count)];
    }

    private static Dictionary<string, double> Tournament(
        IReadOnlyList<(Dictionary<string, double> Genome, double Score)> scored, int size, Random rng)
    {
        var best = scored[rng.Next(scored.Count)];
        for (var i = 1; i < size; i++)
        {
            var challenger = scored[rng.Next(scored.Count)];
            if (challenger.Score > best.Score) best = challenger;
        }
        return best.Genome;
    }

    private static string Key(IReadOnlyDictionary<string, double> genome) =>
        string.Join("|", genome.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value:R}"));
}
