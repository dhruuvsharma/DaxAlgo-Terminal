using System.Globalization;
using TradingTerminal.Backtest.Cli;
using TradingTerminal.Backtest.Cli.Output;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Ml;
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
    "run"          => await RunBacktestAsync(args[1..]),
    "synth"        => await SynthAsync(args[1..]),
    "sweep"        => await SweepAsync(args[1..]),
    "walkforward"  => await WalkForwardAsync(args[1..]),
    "mc"           => await MonteCarloAsync(args[1..]),
    "tca"          => await TcaAsync(args[1..]),
    "features"     => await FeaturesAsync(args[1..]),
    _              => UnknownCommand(args[0]),
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
    var source = (a.Optional("source") ?? "parquet").ToLowerInvariant();
    var output = a.Optional("output") ?? "./bt-results";

    BacktestConfig config;
    BacktestSession session;

    if (source is "store" or "localstore")
    {
        var from = a.Date("from") ?? throw new ArgumentException("--from is required for --source store");
        var to = a.Date("to") ?? throw new ArgumentException("--to is required for --source store");
        var sqlitePath = a.Optional("sqlite-path");
        var postgresConn = a.Optional("postgres-conn");

        var instrumentId = StoreFactory.ResolveSymbol(sqlitePath, postgresConn, symbol);
        if (instrumentId is null)
        {
            Console.Error.WriteLine($"Symbol '{symbol}' not found in the canonical store. " +
                "Run the WPF app and subscribe the symbol on the active broker first.");
            return 2;
        }

        var store = StoreFactory.Open(sqlitePath, postgresConn);
        config = new BacktestConfig(
            Contract: Contract.UsStock(symbol),
            TickDataPath: string.Empty,
            FromUtc: from,
            ToUtc: to,
            TickSize: a.Double("tick-size", 0.01),
            SlippageTicks: a.Int("slippage-ticks", 0),
            ContractMultiplier: a.Double("multiplier", 1.0),
            StartingCash: a.Double("starting-cash", 100_000),
            FeeModel: ResolveFeeModel(a),
            Source: BacktestDataSource.LocalStore,
            InstrumentId: instrumentId.Value);
        session = new BacktestSession(store);
        Console.WriteLine($"Running {strategyId} on {symbol} from local store " +
            $"[{from:o} → {to:o}], instrument {instrumentId.Value}");
    }
    else
    {
        var dataPath = a.Required("data");
        var tradesPath = a.Optional("trades");   // optional real trade tape merged with the quotes
        config = new BacktestConfig(
            Contract: Contract.UsStock(symbol),
            TickDataPath: dataPath,
            FromUtc: a.Date("from"),
            ToUtc: a.Date("to"),
            TickSize: a.Double("tick-size", 0.01),
            SlippageTicks: a.Int("slippage-ticks", 0),
            ContractMultiplier: a.Double("multiplier", 1.0),
            StartingCash: a.Double("starting-cash", 100_000),
            FeeModel: ResolveFeeModel(a),
            TradeDataPath: tradesPath);
        session = new BacktestSession();
        Console.WriteLine(tradesPath is null
            ? $"Running {strategyId} on {symbol} from {dataPath} (quotes only — synthetic L1 for tape strategies)"
            : $"Running {strategyId} on {symbol} from {dataPath} + trade tape {tradesPath} (real prints)");
    }

    var strategy = ResolveStrategy(strategyId, config.Contract);
    var result = await session.RunAsync(config, strategy);

    await ResultWriter.WriteAsync(output, result, CancellationToken.None);

    PrintSummary(result);
    Console.WriteLine($"Wrote results to {Path.GetFullPath(output)}");
    return 0;
}

// Resolves a strategy by id: host-built-in strategies first, then any loaded plugin (e.g. sigmaIcFlow
// once packaged as a plugin, or any third-party plugin dropped into {exe}/plugins). sigmaIcFlow's
// engine moved into the SigmaIcFlow plugin, so it is no longer a built-in here — it returns through
// the plugin path once its DLL is present.
static IBacktestStrategy ResolveStrategy(string id, Contract contract)
{
    IBacktestStrategy? builtin = id.ToLowerInvariant() switch
    {
        "buyandhold" or "buy-and-hold" => new BuyAndHoldStrategy(contract),
        "meanreversion" or "mean-reversion" => new MeanReversionStrategy(contract),
        "donchianbreakout" or "donchian" or "breakout" => new DonchianBreakoutStrategy(contract),
        "ornsteinuhlenbeck" or "ou" => new OrnsteinUhlenbeckStrategy(contract),
        // Index baselines
        "voltarget" or "voltargeting" => new VolatilityTargetedStrategy(contract),
        // L2 / depth-of-market themed
        "vpin" or "toxicity" => new OrderFlowToxicityStrategy(contract),
        "orderflowcube" or "ofcube" or "cube" => new OrderFlowCubeStrategy(contract),
        "orderflowsurfacespike" or "ofss" or "surfacespike" or "surface" => new OrderFlowSurfaceSpikeStrategy(contract),
        "imbalanceheatfront" or "ihf" or "heatfront" => new ImbalanceHeatFrontStrategy(contract),
        "indexkscoresurface" or "kscore" or "indexkscore" => new IndexKScoreSurfaceStrategy(contract),
        "filteredorderflow" or "fof" or "obit" => new FilteredOrderFlowStrategy(contract),
        _ => null,
    };

    return builtin
        ?? PluginStrategies.TryCreate(id, contract)
        ?? throw new ArgumentException(UnknownStrategyMessage(id));
}

static string UnknownStrategyMessage(string id)
{
    const string builtins = "buyAndHold, meanReversion, donchianBreakout, ornsteinUhlenbeck, volTarget, " +
        "vpin, orderFlowCube, orderFlowSurfaceSpike, imbalanceHeatFront, indexKScoreSurface, filteredOrderFlow";
    var plugins = PluginStrategies.AvailableIds;
    var pluginPart = plugins.Count > 0 ? $" Plugins: {string.Join(", ", plugins)}." : " (no plugins loaded)";
    return $"Unknown strategy '{id}'. Built-in: {builtins}.{pluginPart}";
}

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
        "ornsteinuhlenbeck" or "ou" => BuildOuGrid(contract, a),
        _ => throw new ArgumentException($"Sweep doesn't know parameters for '{strategyId}'. Try meanReversion, donchianBreakout, or ornsteinUhlenbeck."),
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

static int[] ParseIntList(string raw) =>
    raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
        .ToArray();

static double[] ParseDoubleList(string raw) =>
    raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
        .ToArray();

// Walk-forward needs to instantiate each strategy FRESH per window — strategies are
// stateful (rolling indicators, position trackers), so reusing an instance across
// train→test→next-window would leak state. Grid builders live in
// Infrastructure.Backtest.WalkForwardGridBuilders so the App's Backtest analysis tab
// can call them too.
static IReadOnlyList<(string Label, Func<Contract, IBacktestStrategy> Builder)> BuildWalkForwardGrid(
    string strategyId, Contract contract, Args a) => strategyId.ToLowerInvariant() switch
{
    "meanreversion" or "mean-reversion" => WalkForwardGridBuilders.MeanReversion(
        ParseIntList(a.Optional("lookback") ?? "50,100,200"),
        ParseDoubleList(a.Optional("entry") ?? "0.05,0.10,0.20"),
        ParseDoubleList(a.Optional("stop") ?? "0.20,0.40"),
        a.Int("qty", 1)),
    "donchianbreakout" or "donchian" or "breakout" => WalkForwardGridBuilders.Donchian(
        ParseIntList(a.Optional("lookback") ?? "50,100,200"),
        ParseDoubleList(a.Optional("trail") ?? "0.10,0.20,0.40"),
        a.Int("qty", 1)),
    "ornsteinuhlenbeck" or "ou" => WalkForwardGridBuilders.OrnsteinUhlenbeck(
        ParseIntList(a.Optional("lookback") ?? "300,500,1000"),
        ParseDoubleList(a.Optional("entry-z") ?? "1.5,2.0,2.5"),
        a.Int("qty", 1)),
    _ => throw new ArgumentException($"Walk-forward grid not defined for '{strategyId}'."),
};

static async Task<int> WalkForwardAsync(string[] argv)
{
    var a = new Args(argv);
    var strategyId = a.Required("strategy").ToLowerInvariant();
    var symbol = a.Required("symbol");
    var dataPath = a.Required("data");
    var windows = Math.Max(2, a.Int("windows", 5));
    var trainFraction = Math.Clamp(a.Double("train-fraction", 0.7), 0.1, 0.95);
    var output = a.Optional("output") ?? "walkforward.csv";
    var maxParallel = Math.Max(1, a.Int("parallel", Environment.ProcessorCount));

    // Scan once to learn the dataset's time range. For typical files this is cheap; for
    // huge files, parquet's row-group metadata would let us short-circuit, but the existing
    // reader streams ticks so a single pass is good enough.
    DateTime? minTs = null, maxTs = null;
    long tickCount = 0;
    await foreach (var t in ParquetTickReader.ReadAsync(dataPath, ct: CancellationToken.None))
    {
        minTs ??= t.TimestampUtc;
        maxTs = t.TimestampUtc;
        tickCount++;
    }
    if (minTs is null || maxTs is null || tickCount == 0)
    {
        Console.Error.WriteLine("Dataset is empty.");
        return 2;
    }

    var totalSpan = maxTs.Value - minTs.Value;
    var winSpan = totalSpan / windows;
    var contract = Contract.UsStock(symbol);

    var baseConfig = new BacktestConfig(
        Contract: contract,
        TickDataPath: dataPath,
        TickSize: a.Double("tick-size", 0.01),
        SlippageTicks: a.Int("slippage-ticks", 0),
        ContractMultiplier: a.Double("multiplier", 1.0),
        StartingCash: a.Double("starting-cash", 100_000));

    var grid = BuildWalkForwardGrid(strategyId, contract, a);

    Console.WriteLine($"Walk-forward: {windows} windows × {grid.Count} configs, train_frac={trainFraction:F2}");
    Console.WriteLine($"  data span: {minTs.Value:O} → {maxTs.Value:O}");

    var rows = new List<string>
    {
        "Window,TrainFromUtc,TrainToUtc,TestFromUtc,TestToUtc,BestParams,TrainSharpe,OosTrades,OosReturn,OosSharpe,OosMaxDrawdown,OosEndingCash"
    };
    var ic = CultureInfo.InvariantCulture;
    using var gate = new SemaphoreSlim(maxParallel);

    for (var w = 0; w < windows; w++)
    {
        var winStart = minTs.Value + winSpan * w;
        var winEnd = (w == windows - 1) ? maxTs.Value : winStart + winSpan;
        var trainCutoff = winStart + (winEnd - winStart) * trainFraction;

        var trainResults = new List<(string Label, Func<Contract, IBacktestStrategy> Build, double Sharpe)>();
        var trainTasks = grid.Select(async cell =>
        {
            await gate.WaitAsync();
            try
            {
                var cfg = baseConfig with { FromUtc = winStart, ToUtc = trainCutoff };
                var s = new BacktestSession();
                var r = await s.RunAsync(cfg, cell.Builder(contract));
                lock (trainResults) trainResults.Add((cell.Label, cell.Builder, r.Stats?.Sharpe ?? double.MinValue));
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(trainTasks);

        var best = trainResults.OrderByDescending(t => t.Sharpe).First();
        var oosCfg = baseConfig with { FromUtc = trainCutoff, ToUtc = winEnd };
        var oosResult = await new BacktestSession().RunAsync(oosCfg, best.Build(contract));
        var os = oosResult.Stats;

        rows.Add(string.Join(",", new[]
        {
            w.ToString(ic),
            winStart.ToString("O", ic),
            trainCutoff.ToString("O", ic),
            trainCutoff.ToString("O", ic),
            winEnd.ToString("O", ic),
            Escape(best.Label),
            best.Sharpe.ToString("F4", ic),
            (os?.TradeCount ?? 0).ToString(ic),
            (os?.TotalReturn ?? 0).ToString("F6", ic),
            (os?.Sharpe ?? 0).ToString("F4", ic),
            (os?.MaxDrawdown ?? 0).ToString("F6", ic),
            oosResult.EndingCash.ToString("F4", ic),
        }));
        Console.WriteLine($"  W{w}: best train={best.Label} (sharpe {best.Sharpe:F2}) → OOS trades={os?.TradeCount ?? 0} sharpe={os?.Sharpe ?? 0:F2}");
    }

    await File.WriteAllLinesAsync(output, rows);
    Console.WriteLine($"Wrote {rows.Count - 1} window rows to {Path.GetFullPath(output)}");
    return 0;
}

static async Task<int> MonteCarloAsync(string[] argv)
{
    var a = new Args(argv);
    var tradesCsv = a.Required("trades");
    var simulations = a.Int("simulations", 10_000);
    var startingCash = a.Double("starting-cash", 100_000);
    var seed = a.Int("seed", -1);

    if (!File.Exists(tradesCsv))
    {
        Console.Error.WriteLine($"Trades file not found: {tradesCsv}");
        return 2;
    }

    var pnls = new List<double>();
    int grossPnlIdx = -1;
    foreach (var (line, lineNo) in await File.ReadAllLinesAsync(tradesCsv).ContinueWith(t => t.Result.Select((l, i) => (l, i))))
    {
        if (lineNo == 0)
        {
            var header = line.Split(',');
            for (var i = 0; i < header.Length; i++)
                if (header[i].Equals("GrossPnl", StringComparison.OrdinalIgnoreCase) ||
                    header[i].Equals("gross_pnl", StringComparison.OrdinalIgnoreCase))
                    grossPnlIdx = i;
            if (grossPnlIdx < 0)
            {
                Console.Error.WriteLine("Trades CSV must contain a GrossPnl column.");
                return 2;
            }
            continue;
        }
        var cells = line.Split(',');
        if (cells.Length <= grossPnlIdx) continue;
        if (double.TryParse(cells[grossPnlIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var pnl))
            pnls.Add(pnl);
    }

    if (pnls.Count == 0)
    {
        Console.Error.WriteLine("No parseable trades.");
        return 2;
    }

    Console.WriteLine($"Monte Carlo: {simulations} simulations × {pnls.Count} trades");
    var result = MonteCarlo.Run(pnls, startingCash, simulations, seed);

    var ic = CultureInfo.InvariantCulture;
    Console.WriteLine();
    Console.WriteLine("                            P5         P25         P50         P75         P95");
    Console.WriteLine($"  Final equity     {Fmt(result.FinalEquityPercentiles, "F2", ic)}");
    Console.WriteLine($"  Sharpe           {Fmt(result.SharpePercentiles,      "F4", ic)}");
    Console.WriteLine($"  Max drawdown     {Fmt(result.MaxDrawdownPercentiles, "F4", ic)}");
    Console.WriteLine();
    Console.WriteLine($"  Mean final equity   : {result.MeanFinalEquity.ToString("F2", ic)}  (σ {result.StdFinalEquity.ToString("F2", ic)})");
    Console.WriteLine($"  Mean Sharpe         : {result.MeanSharpe.ToString("F4", ic)}  (σ {result.StdSharpe.ToString("F4", ic)})");
    Console.WriteLine($"  Mean max drawdown   : {result.MeanMaxDrawdown.ToString("P2", ic)}");
    Console.WriteLine($"  P(profit > 0)       : {result.ProbabilityOfProfit.ToString("P1", ic)}");
    return 0;
}

static string Fmt(IReadOnlyList<double> p, string fmt, CultureInfo ic) =>
    string.Join("  ", p.Select(v => v.ToString(fmt, ic).PadLeft(10)));

static async Task<int> TcaAsync(string[] argv)
{
    var a = new Args(argv);
    var resultsDir = a.Required("results");
    var fillsPath = Path.Combine(resultsDir, "fills.csv");
    if (!File.Exists(fillsPath))
    {
        Console.Error.WriteLine($"Fills file not found: {fillsPath}");
        Console.Error.WriteLine("Run 'daxalgo-backtest run' first — it now emits fills.csv alongside trades.csv.");
        return 2;
    }

    var fills = new List<FillRecord>();
    var lines = await File.ReadAllLinesAsync(fillsPath);
    for (var i = 1; i < lines.Length; i++)
    {
        var c = lines[i].Split(',');
        if (c.Length < 7) continue;
        fills.Add(new FillRecord(
            TimestampUtc: DateTime.Parse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ClientOrderId: c[1],
            Side: Enum.Parse<OrderSide>(c[2]),
            Quantity: long.Parse(c[3], CultureInfo.InvariantCulture),
            Price: double.Parse(c[4], CultureInfo.InvariantCulture),
            MidAtFill: double.Parse(c[5], CultureInfo.InvariantCulture),
            Liquidity: Enum.Parse<LiquidityFlag>(c[6])));
    }
    if (fills.Count == 0)
    {
        Console.Error.WriteLine("No fills to analyse.");
        return 2;
    }

    var report = TransactionCostAnalysis.Compute(fills);
    var ic = CultureInfo.InvariantCulture;
    Console.WriteLine($"TCA report: {report.FillCount} fills, total qty {report.TotalQuantity}");
    Console.WriteLine($"  TWAP mid              : {report.TwapMid.ToString("F4", ic)}");
    Console.WriteLine($"  VWAP fill             : {report.VwapFill.ToString("F4", ic)}");
    Console.WriteLine($"  Implementation shortfall: {report.ImplementationShortfall.ToString("F4", ic)}  (signed; + = cost vs benchmark)");
    Console.WriteLine($"  Mean slippage         : {report.MeanSlippage.ToString("F4", ic)}");
    Console.WriteLine($"  VWAP-weighted slip    : {report.VwapSlippage.ToString("F4", ic)}");
    Console.WriteLine($"  Slippage P50 / P90 / P99: {report.SlippageP50.ToString("F4", ic)} / {report.SlippageP90.ToString("F4", ic)} / {report.SlippageP99.ToString("F4", ic)}");
    Console.WriteLine($"  Maker / Taker mix     : {report.MakerFraction.ToString("P1", ic)} / {report.TakerFraction.ToString("P1", ic)}");
    Console.WriteLine();
    Console.WriteLine("  Hour  Fills  MeanSlip   MakerFrac");
    foreach (var h in report.ByHourUtc)
        Console.WriteLine($"  {h.Hour,4:D2}  {h.Fills,5:D}  {h.MeanSlippage,8:F4}  {h.MakerFraction,8:P1}");

    var outPath = a.Optional("output");
    if (outPath is not null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine($"Wrote JSON report to {Path.GetFullPath(outPath)}");
    }
    return 0;
}

static async Task<int> FeaturesAsync(string[] argv)
{
    var a = new Args(argv);
    var dataPath = a.Required("data");
    var output = a.Optional("output") ?? "features.csv";
    var barTicks = a.Int("bar-ticks", 100);
    var volWindow = a.Int("vol-window", 20);
    var upper = a.Double("upper-barrier", 0.10);
    var lower = a.Double("lower-barrier", 0.10);
    var timeout = a.Int("timeout-bars", 20);

    if (!File.Exists(dataPath))
    {
        Console.Error.WriteLine($"Data file not found: {dataPath}");
        return 2;
    }

    Console.WriteLine($"Loading ticks from {dataPath}…");
    var ticks = new List<Tick>();
    await foreach (var t in ParquetTickReader.ReadAsync(dataPath))
        ticks.Add(t);

    Console.WriteLine($"Aggregating {ticks.Count} ticks → {barTicks}-tick bars + features…");
    var bars = FactorComputation.ComputeBars(ticks, barTicks, volWindow);

    // For triple-barrier we need a high/low per bar. Recompute from the raw ticks since
    // ComputeBars only retains close. (Pure additional pass; could be merged into
    // ComputeBars if we ever care about CPU here.)
    var highs = new double[bars.Count];
    var lows = new double[bars.Count];
    for (var b = 0; b < bars.Count; b++)
    {
        double hi = double.MinValue, lo = double.MaxValue;
        for (var i = b * barTicks; i < (b + 1) * barTicks; i++)
        {
            var mid = (ticks[i].Bid + ticks[i].Ask) * 0.5;
            if (mid > hi) hi = mid;
            if (mid < lo) lo = mid;
        }
        highs[b] = hi; lows[b] = lo;
    }
    var indexed = bars.Select((b, i) => (Bar: b, Index: i)).ToArray();
    var labelled = TripleBarrierLabeler.Apply(
        indexed,
        close: x => x.Bar.Close,
        high: x => highs[x.Index],
        low: x => lows[x.Index],
        upperBarrier: upper,
        lowerBarrier: lower,
        timeoutBars: timeout);

    var ic = CultureInfo.InvariantCulture;
    var lines = new List<string>(bars.Count + 1)
    {
        "timestamp_utc,close,log_return,rolling_vol,microprice_dev,queue_imbalance,spread,label,bars_to_outcome"
    };
    foreach (var lb in labelled)
    {
        var b = lb.Bar.Bar;
        lines.Add(string.Join(",", new[]
        {
            b.TimestampUtc.ToString("O", ic),
            b.Close.ToString("F6", ic),
            b.LogReturn.ToString("F8", ic),
            b.RollingVol.ToString("F8", ic),
            b.MicropriceDeviation.ToString("F8", ic),
            b.QueueImbalance.ToString("F6", ic),
            b.Spread.ToString("F8", ic),
            ((int)lb.Label).ToString(ic),
            lb.BarsToOutcome.ToString(ic),
        }));
    }
    await File.WriteAllLinesAsync(output, lines);

    var pos = labelled.Count(l => l.Label == TripleBarrierLabeler.Label.Positive);
    var neg = labelled.Count(l => l.Label == TripleBarrierLabeler.Label.Negative);
    var neu = labelled.Count - pos - neg;
    Console.WriteLine($"Wrote {labelled.Count} rows to {Path.GetFullPath(output)}");
    Console.WriteLine($"  Labels:  +1 {pos}  ({(double)pos / labelled.Count:P1})   0 {neu}  ({(double)neu / labelled.Count:P1})   -1 {neg}  ({(double)neg / labelled.Count:P1})");
    return 0;
}

static void PrintHelp()
{
    Console.WriteLine("daxalgo-backtest run \\");
    Console.WriteLine("    --strategy <id>          Strategy id (buyAndHold | meanReversion)");
    Console.WriteLine("    --symbol <ticker>        Instrument symbol");
    Console.WriteLine("    [--source parquet|store] Tick source (default parquet)");
    Console.WriteLine("    --data <path.parquet>    Quote tick file (required when --source parquet)");
    Console.WriteLine("    [--trades <path.parquet>] Optional real trade tape merged with the quotes (q=1.0 for tape strategies)");
    Console.WriteLine("    [--from <UTC date>]      Required when --source store");
    Console.WriteLine("    [--to <UTC date>]        Required when --source store");
    Console.WriteLine("    [--sqlite-path <path>]   Override SQLite store path (default: %LOCALAPPDATA%\\DaxAlgoTerminal\\marketdata.db)");
    Console.WriteLine("    [--postgres-conn <str>]  Use Postgres/TimescaleDB store instead of SQLite");
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
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest walkforward \\");
    Console.WriteLine("    --strategy <id>          meanReversion | donchianBreakout | microprice | ou");
    Console.WriteLine("    --symbol <ticker>");
    Console.WriteLine("    --data <path.parquet>");
    Console.WriteLine("    [--windows <n>]          Number of rolling windows (default 5)");
    Console.WriteLine("    [--train-fraction <f>]   Fraction of each window used for training (default 0.7)");
    Console.WriteLine("    [--output <path.csv>]    Default walkforward.csv");
    Console.WriteLine("    [--parallel <n>]         Default = CPU count");
    Console.WriteLine("    [grid params]            Same as 'sweep' for the chosen strategy");
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest mc \\");
    Console.WriteLine("    --trades <path.csv>      Trades CSV from a prior 'run'");
    Console.WriteLine("    [--simulations <n>]      Default 10000");
    Console.WriteLine("    [--starting-cash <n>]    Default 100000");
    Console.WriteLine("    [--seed <int>]           Default -1 (non-deterministic)");
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest tca \\");
    Console.WriteLine("    --results <dir>          Backtest results dir (must contain fills.csv)");
    Console.WriteLine("    [--output <path.json>]   Optional: write full report as JSON");
    Console.WriteLine();
    Console.WriteLine("daxalgo-backtest features \\");
    Console.WriteLine("    --data <path.parquet>    Tick data");
    Console.WriteLine("    [--output <path.csv>]    Default features.csv");
    Console.WriteLine("    [--bar-ticks <n>]        Ticks per aggregated bar (default 100)");
    Console.WriteLine("    [--vol-window <n>]       Bars used in rolling-vol (default 20)");
    Console.WriteLine("    [--upper-barrier <px>]   Take-profit barrier from entry (default 0.10)");
    Console.WriteLine("    [--lower-barrier <px>]   Stop-loss  barrier from entry (default 0.10)");
    Console.WriteLine("    [--timeout-bars <n>]     Vertical barrier in bars   (default 20)");
}
