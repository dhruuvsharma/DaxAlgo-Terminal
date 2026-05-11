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
    _ => throw new ArgumentException(
        $"Unknown strategy '{id}'. Available: buyAndHold, meanReversion.")
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
}
