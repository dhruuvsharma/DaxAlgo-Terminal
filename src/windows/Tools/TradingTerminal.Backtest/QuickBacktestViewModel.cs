using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
#if WINDOWS
using System.Windows;
#endif
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.Backtest;

/// <summary>How the Quick-backtest sources its replay data.</summary>
public enum QuickBacktestDataMode
{
    /// <summary>Pull the real historical trade tape from a broker that exposes one (Binance via
    /// <c>aggTrades</c>) and synthesize a tight L1 quote around each real print. Tape-primary
    /// strategies (SigmaIcFlow) then run at full <see cref="FeedQuality.RealTape"/> quality — a
    /// genuine backtest. Depth/OBI still cannot participate (the engine does not replay L2).</summary>
    FullTapeRealTrades,

    /// <summary>Pull OHLCV bars and synthesize four ticks per bar. Portable to any broker, but there
    /// are no real prints, so a tape-primary strategy runs in the discounted synthetic-L1 mode
    /// (q ≈ 0.4). A rough P&amp;L sniff, not a faithful evaluation.</summary>
    BarSynthetic,
}

/// <summary>
/// One-click backtest launched from the Strategy-catalog "Quick backtest" item. Customised so a
/// tape-primary strategy (SigmaIcFlow) can be backtested <em>properly</em>: with
/// <see cref="QuickBacktestDataMode.FullTapeRealTrades"/> + Binance it pulls the real historical tape
/// (<c>/api/v3/aggTrades</c>, exact aggressor from the maker flag), synthesizes a one-tick L1 around
/// each real print so the fill model + cost gate have a spread, and replays both through the shared
/// engine (<see cref="IBacktestSession"/>) — every flow signal runs at full quality. Falls back to
/// bar-synthesized ticks for brokers without a historical tape.
/// </summary>
public sealed partial class QuickBacktestViewModel : ViewModelBase, IDisposable
{
    private readonly IBacktestStrategyRegistry _registry;
    private readonly IBacktestSession _session;
    private readonly IBrokerSelector _brokers;
    private readonly ILogger<QuickBacktestViewModel> _logger;
    private CancellationTokenSource? _runCts;

    private BacktestStrategyOption? _option;

    public QuickBacktestViewModel(
        IBacktestStrategyRegistry registry,
        IBacktestSession session,
        IBrokerSelector brokers,
        ILogger<QuickBacktestViewModel> logger)
    {
        _registry = registry;
        _session = session;
        _brokers = brokers;
        _logger = logger;

        BarSizes = new ObservableCollection<BarSize>(new[]
        {
            BarSize.OneHour, BarSize.FifteenMinutes, BarSize.FiveMinutes, BarSize.OneDay,
        });
        Lookbacks = new ObservableCollection<LookbackOption>(new[]
        {
            new LookbackOption("Last 1 hour", TimeSpan.FromHours(1)),
            new LookbackOption("Last 2 hours", TimeSpan.FromHours(2)),
            new LookbackOption("Last 4 hours", TimeSpan.FromHours(4)),
            new LookbackOption("Last 12 hours", TimeSpan.FromHours(12)),
            new LookbackOption("Last 1 day", TimeSpan.FromDays(1)),
            new LookbackOption("Last 1 week", TimeSpan.FromDays(7)),
            new LookbackOption("Last 1 month", TimeSpan.FromDays(30)),
            new LookbackOption("Last 1 year", TimeSpan.FromDays(365)),
        });
        DataModes = new ObservableCollection<QuickBacktestDataMode>(new[]
        {
            QuickBacktestDataMode.FullTapeRealTrades, QuickBacktestDataMode.BarSynthetic,
        });
        Brokers = new ObservableCollection<BrokerKind>(_brokers.AvailableKinds);
        Instruments = new ObservableCollection<SignalInstrument>();

        Trades = new ObservableCollection<Trade>();
        EquityCurve = new ObservableCollection<EquityPoint>();

        _selectedBroker = PickDefaultBroker();
        _selectedBarSize = BarSize.OneHour;
        _selectedDataMode = QuickBacktestDataMode.BarSynthetic;
        _selectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromDays(365));
        RebuildInstrumentsFor(_selectedBroker);
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<BarSize> BarSizes { get; }
    public ObservableCollection<LookbackOption> Lookbacks { get; }
    public ObservableCollection<QuickBacktestDataMode> DataModes { get; }
    public ObservableCollection<BrokerKind> Brokers { get; }
    public ObservableCollection<Trade> Trades { get; }
    public ObservableCollection<EquityPoint> EquityCurve { get; }

    /// <summary>Display name of the live strategy being backtested — shown in the header.</summary>
    [ObservableProperty] private string _strategyDisplayName = "Strategy";

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private BarSize _selectedBarSize;
    [ObservableProperty] private LookbackOption _selectedLookback;
    [ObservableProperty] private BrokerKind _selectedBroker;
    [ObservableProperty] private QuickBacktestDataMode _selectedDataMode;

    /// <summary>Per-fill cost in basis points (round-turn modelled per side). Binance spot taker ≈ 7.5 bps.</summary>
    [ObservableProperty] private double _feeBps = 7.5;

    /// <summary>Price increment for the synthetic L1 spread + the fill model. Crypto USDT pairs ≈ 0.01.</summary>
    [ObservableProperty] private double _tickSize = 0.01;

    /// <summary>Safety cap on how many real prints the full-tape pull fetches (each REST page = 1000).</summary>
    [ObservableProperty] private int _maxTrades = 150_000;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _feedQuality;

    [ObservableProperty] private BacktestStatistics? _stats;
    [ObservableProperty] private double _totalPnl;

    public bool IsFullTape => SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades;
    public bool IsBarSynthetic => SelectedDataMode == QuickBacktestDataMode.BarSynthetic;

    partial void OnSelectedDataModeChanged(QuickBacktestDataMode value)
    {
        OnPropertyChanged(nameof(IsFullTape));
        OnPropertyChanged(nameof(IsBarSynthetic));
    }

    partial void OnSelectedBrokerChanged(BrokerKind value) => RebuildInstrumentsFor(value);

    /// <summary>Raised after a run completes so the view can redraw the ScottPlot equity curve.</summary>
    public event EventHandler? EquityCurveUpdated;

    /// <summary>
    /// Binds this window to a live strategy by its engine-side backtest id and kicks off the first run.
    /// <paramref name="preferFullTape"/> (true for tape-primary strategies like SigmaIcFlow) defaults
    /// the window to Binance + real-tape mode + a liquid crypto pair so the auto-run is a proper backtest.
    /// Returns false (with a status message) when the strategy has no backtest counterpart.
    /// </summary>
    public bool Initialize(string? backtestStrategyId, string displayName, bool preferFullTape)
    {
        StrategyDisplayName = displayName;

        if (string.IsNullOrWhiteSpace(backtestStrategyId))
        {
            Status = $"'{displayName}' has no backtest counterpart wired up yet.";
            return false;
        }

        _option = _registry.Find(backtestStrategyId);
        if (_option is null)
        {
            Status = $"No backtest strategy registered for id '{backtestStrategyId}'.";
            return false;
        }

        if (preferFullTape && _brokers.IsAvailable(BrokerKind.Binance))
        {
            SelectedBroker = BrokerKind.Binance;          // rebuilds Instruments to the crypto universe
            SelectedDataMode = QuickBacktestDataMode.FullTapeRealTrades;
            SelectedLookback = Lookbacks.First(l => l.Duration == TimeSpan.FromHours(2));
            SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "BTCUSDT") ?? Instruments.FirstOrDefault();
        }

        if (RunCommand.CanExecute(null)) RunCommand.Execute(null);
        return true;
    }

    /// <summary>Prefer Binance (real tape) → any other connected real broker → Simulated synthetic.</summary>
    private BrokerKind PickDefaultBroker()
    {
        if (_brokers.IsAvailable(BrokerKind.Binance)) return BrokerKind.Binance;
        foreach (var k in _brokers.Connected)
            if (k != BrokerKind.Simulated) return k;
        if (_brokers.IsAvailable(BrokerKind.Simulated)) return BrokerKind.Simulated;
        return Brokers.FirstOrDefault();
    }

    /// <summary>Binance shows the crypto universe; every other broker uses the shared signal catalog.</summary>
    private void RebuildInstrumentsFor(BrokerKind broker)
    {
        var keep = SelectedInstrument?.Contract.Symbol;
        Instruments.Clear();
        var source = broker == BrokerKind.Binance ? CryptoInstruments() : SignalInstrumentCatalog.All;
        foreach (var i in source) Instruments.Add(i);
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == keep) ?? Instruments.FirstOrDefault();
    }

    private static IReadOnlyList<SignalInstrument> CryptoInstruments() => new[]
    {
        Crypto("BTCUSDT", "BTC/USDT — Bitcoin"),
        Crypto("ETHUSDT", "ETH/USDT — Ether"),
        Crypto("SOLUSDT", "SOL/USDT — Solana"),
        Crypto("BNBUSDT", "BNB/USDT — BNB"),
        Crypto("XRPUSDT", "XRP/USDT — XRP"),
        Crypto("DOGEUSDT", "DOGE/USDT — Dogecoin"),
    };

    private static SignalInstrument Crypto(string symbol, string display) =>
        new(display, "Crypto (Binance)", new Contract(symbol, "CRYPTO", "BINANCE", "USDT", PrimaryExchange: string.Empty), BrokerKind.Binance);

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        if (_option is null) { Status ??= "Strategy not initialised."; return; }
        if (SelectedInstrument is null) { Status = "Pick an instrument."; return; }
        if (!_brokers.IsAvailable(SelectedBroker)) { Status = $"{SelectedBroker} is not registered. Pick another data source."; return; }
        if (TickSize <= 0) { Status = "Tick size must be greater than zero."; return; }

        IsRunning = true;
        Trades.Clear();
        EquityCurve.Clear();
        Stats = null;
        TotalPnl = 0;
        FeedQuality = null;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var contract = SelectedInstrument.Contract;
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc - SelectedLookback.Duration;
        string? quotesPath = null;
        string? tradesPath = null;

        try
        {
            var client = _brokers.Get(SelectedBroker);
            BacktestConfig config;

            if (SelectedDataMode == QuickBacktestDataMode.FullTapeRealTrades)
            {
                Status = $"Fetching the real {contract.Symbol} tape from {SelectedBroker} (up to {MaxTrades:N0} prints)…";
                IReadOnlyList<TradeTick> tape;
                try
                {
                    tape = await client.RequestHistoricalTradesAsync(contract, fromUtc, toUtc, MaxTrades, ct).ConfigureAwait(true);
                }
                catch (NotSupportedException)
                {
                    Status = $"{SelectedBroker} has no historical trade tape. Switch the data source to Binance, " +
                             "or change the mode to bar-synthetic (degraded for tape-primary strategies).";
                    return;
                }

                if (tape.Count == 0)
                {
                    Status = $"No trades returned for '{contract.Symbol}' over {SelectedLookback.Label.ToLowerInvariant()}. " +
                             "Try a more liquid pair or a different window.";
                    return;
                }

                Status = $"Replaying {tape.Count:N0} real prints through the engine…";
                quotesPath = Path.Combine(Path.GetTempPath(), $"quick-bt-q-{Guid.NewGuid():N}.parquet");
                tradesPath = Path.Combine(Path.GetTempPath(), $"quick-bt-t-{Guid.NewGuid():N}.parquet");
                await WriteRealTapeAsync(quotesPath, tradesPath, tape, TickSize, SelectedBroker, ct).ConfigureAwait(true);

                config = new BacktestConfig(
                    Contract: contract,
                    TickDataPath: quotesPath,
                    TickSize: TickSize,
                    SlippageTicks: 1,
                    ContractMultiplier: 1,            // crypto spot: PnL is in quote currency, multiplier = 1
                    StartingCash: 100_000,
                    FeeModel: new BpsFeeModel(FeeBps),
                    Source: BacktestDataSource.ParquetFile,
                    TradeDataPath: tradesPath);
                FeedQuality = "Real tape (q = 1.0) — full quality. Depth/OBI excluded (engine is L1-only for depth).";
            }
            else
            {
                Status = $"Fetching {SelectedBarSize} bars from {SelectedBroker}…";
                var bars = await client.RequestHistoricalBarsAsync(contract, SelectedBarSize, SelectedLookback.Duration, ct).ConfigureAwait(true);
                if (bars.Count == 0)
                {
                    Status = $"No bars returned for '{contract.Symbol}' from {SelectedBroker}. Try a different instrument / bar size.";
                    return;
                }

                Status = $"Replaying {bars.Count} {SelectedBarSize} bars through the engine…";
                quotesPath = Path.Combine(Path.GetTempPath(), $"quick-bt-{Guid.NewGuid():N}.parquet");
                await WriteSyntheticTicksAsync(quotesPath, bars, SelectedBarSize.ToTimeSpan(), TickSize, ct).ConfigureAwait(true);

                config = new BacktestConfig(
                    Contract: contract,
                    TickDataPath: quotesPath,
                    TickSize: TickSize,
                    SlippageTicks: 1,
                    ContractMultiplier: 1,
                    StartingCash: 100_000,
                    FeeModel: new BpsFeeModel(FeeBps),
                    Source: BacktestDataSource.ParquetFile);
                FeedQuality = "Synthetic L1 from bars (q ≈ 0.4) — degraded for tape-primary strategies; rough sniff only.";
            }

            var strategy = _option.Create(contract);
            var result = await Task.Run(() => _session.RunAsync(config, strategy, risk: null, ct), ct).ConfigureAwait(true);

            foreach (var t in result.Trades) Trades.Add(t);
            foreach (var p in result.EquityCurve) EquityCurve.Add(p);
            Stats = result.Stats;
            TotalPnl = result.EndingCash - result.StartingCash;
            Status = $"Done. {result.Trades.Count} trades, P&L {TotalPnl.ToString("C2", CultureInfo.CurrentCulture)} " +
                     $"(fees {result.TotalFees.ToString("C2", CultureInfo.CurrentCulture)}).";
            EquityCurveUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quick backtest run failed");
            Status = $"Failed: {ex.Message}";
#if WINDOWS
            MessageBox.Show(ex.Message, "Quick backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
#endif
        }
        finally
        {
            TryDelete(quotesPath);
            TryDelete(tradesPath);
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _runCts?.Cancel();

    /// <summary>Cancels any in-flight run when the window closes so a long backtest doesn't outlive it.</summary>
    public void Dispose()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* run already completed and disposed the CTS */ }
    }

    /// <summary>
    /// Writes the real tape as two time-aligned parquets the engine merges: the trades file carries the
    /// genuine prints (price/size/aggressor) that drive every flow signal at full quality; the quotes
    /// file carries a one-tick L1 straddling each print so the order book can fill and the cost gate has
    /// a spread. Timestamps are forced strictly increasing (Binance stamps to the millisecond, so prints
    /// collide) — the clock requires monotonic time.
    /// </summary>
    private static async Task WriteRealTapeAsync(
        string quotesPath, string tradesPath, IReadOnlyList<TradeTick> tape, double tickSize, BrokerKind source, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var lastTicks = long.MinValue;

        await using var quoteWriter = new ParquetTickWriter(quotesPath);
        await using var tradeWriter = new ParquetTradeWriter(tradesPath);

        long seq = 0;
        foreach (var p in tape)
        {
            ct.ThrowIfCancellationRequested();

            var ts = p.TimestampUtc;
            if (ts.Ticks <= lastTicks) ts = new DateTime(lastTicks + 10, DateTimeKind.Utc); // +1 microsecond
            lastTicks = ts.Ticks;

            var sizeProxy = Math.Max(1, p.Size);
            await quoteWriter.WriteAsync(new Tick(ts, p.Price - half, p.Price + half, sizeProxy, sizeProxy), ct).ConfigureAwait(false);
            await tradeWriter.WriteAsync(
                new TradePrint(InstrumentId.None, ts, ts, p.Price, p.Size, p.Aggressor, source, seq++, EventTimeApproximate: false), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Projects OHLCV bars into a synthetic quote stream (open → low/high by direction → close, four
    /// prints per bar, one-tick-wide). No real prints, so a tape-primary strategy runs degraded.
    /// Mirrors the LSE backtester's synthesizer.
    /// </summary>
    private static async Task WriteSyntheticTicksAsync(
        string path, IReadOnlyList<Bar> bars, TimeSpan barSpan, double tickSize, CancellationToken ct)
    {
        var half = Math.Max(tickSize, 1e-9) / 2.0;
        var step = barSpan / 4;

        await using var writer = new ParquetTickWriter(path);
        foreach (var bar in bars)
        {
            ct.ThrowIfCancellationRequested();

            var path4 = bar.Close >= bar.Open
                ? new[] { bar.Open, bar.Low, bar.High, bar.Close }
                : new[] { bar.Open, bar.High, bar.Low, bar.Close };
            var sizePer = Math.Max(1, bar.Volume / 4);

            for (var i = 0; i < path4.Length; i++)
            {
                var px = path4[i];
                var ts = bar.TimestampUtc + step * i;
                await writer.WriteAsync(new Tick(ts, px - half, px + half, sizePer, sizePer), ct).ConfigureAwait(false);
            }
        }
    }

    private void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete temp file {Path}", path); }
    }
}

/// <summary>A named lookback window for the Quick-backtest control (label + duration).</summary>
public sealed record LookbackOption(string Label, TimeSpan Duration)
{
    public override string ToString() => Label;
}
