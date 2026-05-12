using System.Globalization;
using TradingTerminal.Backtest.Cli;
using TradingTerminal.Backtest.Cli.Output;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
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
        StartingCash: a.Double("starting-cash", 100_000),
        FeeModel: ResolveFeeModel(a));

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
    "microprice" => new MicropriceStrategy(contract),
    "ornsteinuhlenbeck" or "ou" => new OrnsteinUhlenbeckStrategy(contract),
    "avellanedastoikov" or "as" or "marketmaker" => new AvellanedaStoikovStrategy(contract),
    "twap" => new TwapExecutionStrategy(contract, OrderSide.Buy),
    _ => throw new ArgumentException(
        $"Unknown strategy '{id}'. Available: buyAndHold, meanReversion, donchianBreakout, microprice, ornsteinUhlenbeck, avellanedaStoikov, twap.")
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
    Console.WriteLine($"  Calmar              : {s.Calmar.ToString("F2", ic)}");
    Console.WriteLine($"  Omega               : {s.Omega.ToString("F2", ic)}");
    Console.WriteLine($"  Max drawdown        : {s.MaxDrawdown.ToString("P2", ic)}");
    Console.WriteLine($"  Ulcer index         : {s.UlcerIndex.ToString("F4", ic)}");
    Console.WriteLine($"  Recovery factor     : {s.RecoveryFactor.ToString("F2", ic)}");
    Console.WriteLine($"  Win rate            : {s.WinRate.ToString("P1", ic)}");
    Console.WriteLine($"  Profit factor       : {s.ProfitFactor.ToString("F2", ic)}");
    Console.WriteLine($"  Expectancy / trade  : {s.Expectancy.ToString("F4", ic)}");
    Console.WriteLine($"  Max consec. losses  : {s.MaxConsecutiveLosses.ToString(ic)}");
    Console.WriteLine($"  Total fees / rebates: {result.TotalFees.ToString("F4", ic)}");
}

static IFeeModel? ResolveFeeModel(Args a)
{
    var taker = a.Optional("taker-fee");
    var maker = a.Optional("maker-rebate");
    var bps = a.Optional("fee-bps");
    if (bps is not null)
        return new BpsFeeModel(double.Parse(bps, CultureInfo.InvariantCulture));
    if (taker is not null || maker is not null)
        return new MakerTakerFeeModel(
            takerFeePerUnit: double.Parse(taker ?? "0", CultureInfo.InvariantCulture),
            makerRebatePerUnit: double.Parse(maker ?? "0", CultureInfo.InvariantCulture));
    return null;
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

    var varySizes = a.Optional("vary-sizes") != "false";

    await using var writer = new ParquetTickWriter(output);
    for (var i = 0; i < ticks; i++)
    {
        // Mean-reverting random walk so the demo strategies have something to chew on.
        mid += (rng.NextDouble() - 0.5) * spread * 4;
        mid += (startMid - mid) * 0.001;

        // Occasional "spread-widen" event lets market-maker / breakout strategies actually
        // get hit. ~1% of ticks have 3x spread, 1‑bid/1‑ask sizes (a fast quote).
        var burst = rng.NextDouble() < 0.01;
        var thisSpread = burst ? spread * 3 : spread;

        long bidSize = 10, askSize = 10;
        if (varySizes)
        {
            // Sizes ~Poisson-ish around 10, asymmetric so microprice has something to say.
            bidSize = Math.Max(1, (long)Math.Round(5 + rng.NextDouble() * 30));
            askSize = Math.Max(1, (long)Math.Round(5 + rng.NextDouble() * 30));
        }

        await writer.WriteAsync(new Tick(
            origin.AddSeconds(i),
            Bid: mid - thisSpread * 0.5,
            Ask: mid + thisSpread * 0.5,
            BidSize: bidSize,
            AskSize: askSize));
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
        "microprice" => BuildMicropriceGrid(contract, a),
        "ornsteinuhlenbeck" or "ou" => BuildOuGrid(contract, a),
        "avellanedastoikov" or "as" or "marketmaker" => BuildAvellanedaGrid(contract, a),
        _ => throw new ArgumentException($"Sweep doesn't know parameters for '{strategyId}'. Try meanReversion, donchianBreakout, microprice, ornsteinUhlenbeck, or avellanedaStoikov."),
    };

    Console.WriteLine($"Sweep: {grid.Count} configurations on {symbol} (parallel={maxParallel})");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var rows = new System.Collections.Concurrent.ConcurrentBag<string>();
    var ic = CultureInfo.InvariantCulture;
    rows.Add("Label,Trades,TotalReturn,Sharpe,Sortino,Calmar,Omega,MaxDrawdown,UlcerIndex,MaxConsecLosses,WinRate,ProfitFactor,Expectancy,Fees,EndingCash");

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
                (s?.Calmar ?? 0).ToString("F4", ic),
                (s?.Omega ?? 0).ToString("F4", ic),
                (s?.MaxDrawdown ?? 0).ToString("F6", ic),
                (s?.UlcerIndex ?? 0).ToString("F6", ic),
                (s?.MaxConsecutiveLosses ?? 0).ToString(ic),
                (s?.WinRate ?? 0).ToString("F4", ic),
                (s?.ProfitFactor ?? 0).ToString("F4", ic),
                (s?.Expectancy ?? 0).ToString("F6", ic),
                result.TotalFees.ToString("F4", ic),
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

static IReadOnlyList<(string Label, IBacktestStrategy Build)> BuildMicropriceGrid(Contract contract, Args a)
{
    var thresholds = ParseDoubleList(a.Optional("threshold") ?? "0.0005,0.001,0.002");
    var holds = ParseIntList(a.Optional("hold") ?? "20,50,100");
    var qty = a.Int("qty", 1);

    var grid = new List<(string, IBacktestStrategy)>();
    foreach (var t in thresholds)
        foreach (var h in holds)
            grid.Add(($"mp-t{t.ToString(CultureInfo.InvariantCulture)}-h{h}",
                new MicropriceStrategy(contract, t, h, qty)));
    return grid;
}

static IReadOnlyList<(string Label, IBacktestStrategy Build)> BuildOuGrid(Contract contract, Args a)
{
    var lookbacks = ParseIntList(a.Optional("lookback") ?? "300,500,1000");
    var entries = ParseDoubleList(a.Optional("entry-z") ?? "1.5,2.0,2.5");
    var qty = a.Int("qty", 1);

    var grid = new List<(string, IBacktestStrategy)>();
    foreach (var l in lookbacks)
        foreach (var ez in entries)
            grid.Add(($"ou-lk{l}-z{ez.ToString(CultureInfo.InvariantCulture)}",
                new OrnsteinUhlenbeckStrategy(contract, lookback: l, entryZ: ez, quantity: qty)));
    return grid;
}

static IReadOnlyList<(string Label, IBacktestStrategy Build)> BuildAvellanedaGrid(Contract contract, Args a)
{
    var gammas = ParseDoubleList(a.Optional("gamma") ?? "0.05,0.10,0.20");
    var ks = ParseDoubleList(a.Optional("k") ?? "1.0,1.5,3.0");
    var qty = a.Int("qty", 1);

    var grid = new List<(string, IBacktestStrategy)>();
    foreach (var g in gammas)
        foreach (var k in ks)
            grid.Add(($"as-g{g.ToString(CultureInfo.InvariantCulture)}-k{k.ToString(CultureInfo.InvariantCulture)}",
                new AvellanedaStoikovStrategy(contract, gamma: g, k: k, quoteSize: qty)));
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
    Console.WriteLine("    [--taker-fee <n>]        Per-unit taker fee (with --maker-rebate uses MakerTakerFeeModel)");
    Console.WriteLine("    [--maker-rebate <n>]     Per-unit maker rebate (positive value)");
    Console.WriteLine("    [--fee-bps <n>]          Flat bps fee on notional (overrides taker/maker)");
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
