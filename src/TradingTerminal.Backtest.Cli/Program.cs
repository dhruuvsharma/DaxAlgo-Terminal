using System.Globalization;
using TradingTerminal.Backtest.Cli;
using TradingTerminal.Backtest.Cli.Output;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Infrastructure.Backtest.Strategies;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

return args[0] switch
{
    "run"   => await RunBacktestAsync(args[1..]),
    "synth" => await SynthAsync(args[1..]),
    "sweep" => await SweepAsync(args[1..]),
    _       => UnknownCommand(args[0]),
};

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintHelp();
    return 2;
}

static async Task<int> RunBacktestAsync(string[] argv)
{
    var a = new Args(argv);

    var strategyId = a.Required("strategy");
    var symbol = a.Required("symbol");
    var dataPath = a.Required("data");
    var output = a.Optional("output") ?? "./bt-results";

    var config = new BacktestConfig(
        Contract: Contract.UsStock(symbol),
        TickDataPath: dataPath,
        FromUtc: a.Date("from"),
        ToUtc: a.Date("to"),
        TickSize: a.Double("tick-size", 0.01),
        SlippageTicks: a.Int("slippage-ticks", 0),
        ContractMultiplier: a.Double("multiplier", 1.0),
        StartingCash: a.Double("starting-cash", 100_000));

    var strategy = ResolveStrategy(strategyId, config.Contract);

    Console.WriteLine($"Running {strategyId} on {symbol} from {dataPath}");
    var session = new BacktestSession();
    var result = await session.RunAsync(config, strategy);

    await ResultWriter.WriteAsync(output, result, CancellationToken.None);

    PrintSummary(result);
    Console.WriteLine($"Wrote results to {Path.GetFullPath(output)}");
    return 0;
}

static IBacktestStrategy ResolveStrategy(string id, Contract contract) => id.ToLowerInvariant() switch
{
    "buyandhold" or "buy-and-hold" => new BuyAndHoldStrategy(contract),
    "meanreversion" or "mean-reversion" => new MeanReversionStrategy(contract),
    "donchianbreakout" or "donchian" or "breakout" => new DonchianBreakoutStrategy(contract),
    _ => throw new ArgumentException(
        $"Unknown strategy '{id}'. Available: buyAndHold, meanReversion, donchianBreakout.")
};

static void PrintSummary(BacktestResult result)
{
    var s = result.Stats;
    if (s is null) return;
    var ic = CultureInfo.InvariantCulture;
    Console.WriteLine();
    Console.WriteLine($"  Trades              : {s.TradeCount}");
    Console.WriteLine($"  Total return        : {s.TotalReturn.ToString("P2", ic)}");
    Console.WriteLine($"  Sharpe (annualized) : {s.Sharpe.ToString("F2", ic)}");
    Console.WriteLine($"  Sortino             : {s.Sortino.ToString("F2", ic)}");
    Console.WriteLine($"  Max drawdown        : {s.MaxDrawdown.ToString("P2", ic)}");
    Console.WriteLine($"  Win rate            : {s.WinRate.ToString("P1", ic)}");
    Console.WriteLine($"  Profit factor       : {s.ProfitFactor.ToString("F2", ic)}");
    Console.WriteLine($"  Expectancy / trade  : {s.Expectancy.ToString("F4", ic)}");
}

static async Task<int> SynthAsync(string[] argv)
{
    var a = new Args(argv);
    var output = a.Required("output");
    var ticks = a.Int("ticks", 10_000);
    var startMid = a.Double("start-mid", 100.0);
    var spread = a.Double("spread", 0.01);
    var seed = a.Int("seed", 42);

    var rng = new Random(seed);
    var origin = DateTime.UtcNow.Date;
    var mid = startMid;

    await using var writer = new ParquetTickWriter(output);
    for (var i = 0; i < ticks; i++)
    {
        // Mean-reverting random walk so the demo strategies have something to chew on.
        mid += (rng.NextDouble() - 0.5) * spread * 4;
        mid += (startMid - mid) * 0.001;
        await writer.WriteAsync(new Tick(
            origin.AddSeconds(i),
            Bid: mid - spread * 0.5,
            Ask: mid + spread * 0.5,
            BidSize: 10,
            AskSize: 10));
    }
    Console.WriteLine($"Wrote {ticks} synthetic ticks to {Path.GetFullPath(output)}");
    return 0;
}

static async Task<int> SweepAsync(string[] argv)
{
    var a = new Args(argv);
    var strategyId = a.Required("strategy").ToLowerInvariant();
    var symbol = a.Required("symbol");
    var dataPath = a.Required("data");
    var output = a.Optional("output") ?? "sweep-results.csv";
    var maxParallel = Math.Max(1, a.Int("parallel", Environment.ProcessorCount));

    var contract = Contract.UsStock(symbol);
    var baseConfig = new BacktestConfig(
        Contract: contract,
        TickDataPath: dataPath,
        FromUtc: a.Date("from"),
        ToUtc: a.Date("to"),
        TickSize: a.Double("tick-size", 0.01),
        SlippageTicks: a.Int("slippage-ticks", 0),
        ContractMultiplier: a.Double("multiplier", 1.0),
        StartingCash: a.Double("starting-cash", 100_000));

    IReadOnlyList<(string Label, IBacktestStrategy Build)> grid = strategyId switch
    {
        "meanreversion" or "mean-reversion" => BuildMeanReversionGrid(contract, a),
        "donchianbreakout" or "donchian" or "breakout" => BuildDonchianGrid(contract, a),
        _ => throw new ArgumentException($"Sweep doesn't know parameters for '{strategyId}'. Try meanReversion or donchianBreakout."),
    };

    Console.WriteLine($"Sweep: {grid.Count} configurations on {symbol} (parallel={maxParallel})");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var rows = new System.Collections.Concurrent.ConcurrentBag<string>();
    var ic = CultureInfo.InvariantCulture;
    rows.Add("Label,Trades,TotalReturn,Sharpe,Sortino,MaxDrawdown,WinRate,ProfitFactor,Expectancy,EndingCash");

    using var gate = new SemaphoreSlim(maxParallel);
    var tasks = grid.Select(async cell =>
    {
        await gate.WaitAsync();
        try
        {
            var session = new BacktestSession();
            var result = await session.RunAsync(baseConfig, cell.Build);
            var s = result.Stats;
            rows.Add(string.Join(",", new[]
            {
                Escape(cell.Label),
                (s?.TradeCount ?? 0).ToString(ic),
                (s?.TotalReturn ?? 0).ToString("F6", ic),
                (s?.Sharpe ?? 0).ToString("F4", ic),
                (s?.Sortino ?? 0).ToString("F4", ic),
                (s?.MaxDrawdown ?? 0).ToString("F6", ic),
                (s?.WinRate ?? 0).ToString("F4", ic),
                (s?.ProfitFactor ?? 0).ToString("F4", ic),
                (s?.Expectancy ?? 0).ToString("F6", ic),
                result.EndingCash.ToString("F4", ic),
            }));
            Console.WriteLine($"  {cell.Label}: trades={s?.TradeCount ?? 0}, sharpe={s?.Sharpe ?? 0:F2}, ret={s?.TotalReturn ?? 0:P2}");
        }
        finally
        {
            gate.Release();
        }
    });
    await Task.WhenAll(tasks);
    stopwatch.Stop();

    var ordered = rows.OrderBy(r => r.StartsWith("Label,", StringComparison.Ordinal) ? 0 : 1).ToArray();
    await File.WriteAllLinesAsync(output, ordered);
    Console.WriteLine($"Wrote {ordered.Length - 1} rows to {Path.GetFullPath(output)} in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
}

static string Escape(string s) =>
    s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

static IReadOnlyList<(string Label, IBacktestStrategy Build)> BuildMeanReversionGrid(Contract contract, Args a)
{
    var lookbacks = ParseIntList(a.Optional("lookback") ?? "50,100,200");
    var entries = ParseDoubleList(a.Optional("entry") ?? "0.05,0.10,0.20");
    var stops = ParseDoubleList(a.Optional("stop") ?? "0.20,0.40");
    var qty = a.Int("qty", 1);

    var grid = new List<(string, IBacktestStrategy)>();
    foreach (var l in lookbacks)
        foreach (var e in entries)
            foreach (var s in stops)
                grid.Add(($"mr-lk{l}-e{e.ToString(CultureInfo.InvariantCulture)}-s{s.ToString(CultureInfo.InvariantCulture)}",
                    new MeanReversionStrategy(contract, l, e, s, qty)));
    return grid;
}

static IReadOnlyList<(string Label, IBacktestStrategy Build)> BuildDonchianGrid(Contract contract, Args a)
{
    var lookbacks = ParseIntList(a.Optional("lookback") ?? "50,100,200");
    var stops = ParseDoubleList(a.Optional("trail") ?? "0.10,0.20,0.40");
    var qty = a.Int("qty", 1);

    var grid = new List<(string, IBacktestStrategy)>();
    foreach (var l in lookbacks)
        foreach (var s in stops)
            grid.Add(($"don-lk{l}-trail{s.ToString(CultureInfo.InvariantCulture)}",
                new DonchianBreakoutStrategy(contract, l, s, qty)));
    return grid;
}

static int[] ParseIntList(string raw) =>
    raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
        .ToArray();

static double[] ParseDoubleList(string raw) =>
    raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
        .ToArray();

static void PrintHelp()
{
    Console.WriteLine("daxalgo-backtest run \\");
    Console.WriteLine("    --strategy <id>          Strategy id (buyAndHold | meanReversion)");
    Console.WriteLine("    --symbol <ticker>        Instrument symbol");
    Console.WriteLine("    --data <path.parquet>    Tick data file");
    Console.WriteLine("    [--from <UTC date>]");
    Console.WriteLine("    [--to <UTC date>]");
    Console.WriteLine("    [--tick-size <n>]        Default 0.01");
    Console.WriteLine("    [--slippage-ticks <n>]   Default 0");
    Console.WriteLine("    [--multiplier <n>]       Default 1");
    Console.WriteLine("    [--starting-cash <n>]    Default 100000");
    Console.WriteLine("    [--output <dir>]         Default ./bt-results");
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest synth \\");
    Console.WriteLine("    --output <path.parquet>  Where to write the synthetic dataset");
    Console.WriteLine("    [--ticks <n>]            Default 10000");
    Console.WriteLine("    [--start-mid <px>]       Default 100");
    Console.WriteLine("    [--spread <px>]          Default 0.01");
    Console.WriteLine("    [--seed <int>]           Default 42");
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest sweep \\");
    Console.WriteLine("    --strategy <id>          meanReversion | donchianBreakout");
    Console.WriteLine("    --symbol <ticker>");
    Console.WriteLine("    --data <path.parquet>");
    Console.WriteLine("    [--lookback <a,b,c>]     Grid over lookbacks");
    Console.WriteLine("    [--entry <a,b,c>]        meanReversion only");
    Console.WriteLine("    [--stop <a,b,c>]         meanReversion only");
    Console.WriteLine("    [--trail <a,b,c>]        donchianBreakout only");
    Console.WriteLine("    [--qty <n>]              Default 1");
    Console.WriteLine("    [--output <path.csv>]    Default sweep-results.csv");
    Console.WriteLine("    [--parallel <n>]         Default = CPU count");
}
