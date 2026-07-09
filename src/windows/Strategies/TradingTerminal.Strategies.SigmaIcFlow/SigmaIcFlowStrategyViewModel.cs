using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.UI;
using TradingTerminal.Strategies.SigmaIcFlow.Engine;
using Engine = TradingTerminal.Strategies.SigmaIcFlow.Engine;

namespace TradingTerminal.Strategies.SigmaIcFlow;

/// <summary>
/// A single row in the weight-vector table shown on the dashboard. Name → estimated Σ⁻¹·IC weight.
/// </summary>
public sealed record WeightRow(string SignalName, double Weight);

/// <summary>
/// Live signal-mode VM for the Σ⁻¹·IC Order-Flow Optimizer. Instantiates the engine-side
/// <see cref="Engine.ApexScalperStrategy"/> inside <see cref="BuildStrategy"/> and projects
/// <see cref="Engine.ApexScalperStrategy.Latest"/> (<see cref="ApexSnapshotV2"/>) onto
/// observable properties consumed by the dashboard.
///
/// <para>No signal math lives here. The VM is a thin projection layer: it reads snapshot fields,
/// formats them for display, and exposes observable collections. All business logic is in the
/// engine.</para>
/// </summary>
public sealed partial class SigmaIcFlowStrategyViewModel : LiveSignalStrategyViewModelBase
{
    // ── Setup / parameter knobs ──────────────────────────────────────────────────────────────────

    /// <summary>Number of recent internal candles each per-indicator chart draws.</summary>
    [ObservableProperty] private int _maxChartCandles = 60;

    /// <summary>Number of footprint bars the cluster chart renders (render-only, live-tunable).</summary>
    [ObservableProperty] private int _footprintBarsVisible = 10;

    /// <summary>Selectable internal-candle intervals — one engine clock for signals + footprint.</summary>
    public sealed record CandleIntervalOption(string Label, TimeSpan Span)
    {
        public override string ToString() => Label;
    }

    public ObservableCollection<CandleIntervalOption> CandleIntervals { get; } = new(new[]
    {
        // Sub-minute candles are no longer supported — 1m is the minimum reference span.
        new CandleIntervalOption("1m",  TimeSpan.FromMinutes(1)),
        new CandleIntervalOption("2m",  TimeSpan.FromMinutes(2)),
        new CandleIntervalOption("3m",  TimeSpan.FromMinutes(3)),
        new CandleIntervalOption("5m",  TimeSpan.FromMinutes(5)),
        new CandleIntervalOption("10m", TimeSpan.FromMinutes(10)),
        new CandleIntervalOption("15m", TimeSpan.FromMinutes(15)),
        new CandleIntervalOption("30m", TimeSpan.FromMinutes(30)),
    });

    [ObservableProperty] private CandleIntervalOption? _selectedCandleInterval;

    /// <summary>Visible x-axis span of the SIGNALS chart, in minutes. 0 = auto: the window is
    /// MaxChartCandles × candle interval, so the scale tracks the selected timeframe. Render-only
    /// and live-tunable — never touches engine state.</summary>
    [ObservableProperty] private double _chartXSpanMinutes;

    /// <summary>Price grid the footprint rows snap to. Feeds the engine's footprint builder.</summary>
    [ObservableProperty] private string _footprintTickSizeText = "0.25";

    private double FootprintTickSizeParsed =>
        double.TryParse(FootprintTickSizeText, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) && t > 0
            ? t : 0.25;

    // ── v2-specific user knobs (map to ApexV2Options) ───────────────────────────────────────────
    //   Only knobs a practitioner actually adjusts at strategy-open time are exposed here.
    //   The remaining ApexV2Options fields carry sensible dimensionless defaults and are not surfaced.

    /// <summary>Tick-size override; 0 means derive from ATR (engine default).</summary>
    [ObservableProperty] private string _tickSizeOverrideText = "0";

    private double? TickSizeOverrideParsed
    {
        get
        {
            if (!double.TryParse(TickSizeOverrideText, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return null;
            return v > 0 ? v : null;
        }
    }

    /// <summary>Quarter-Kelly fraction (default 0.25).</summary>
    [ObservableProperty] private double _kellyFraction = 0.25;

    /// <summary>Bootstrap composite threshold (absolute |C| required before isotonic curve is trusted).</summary>
    [ObservableProperty] private double _bootstrapThreshold = 1.0;

    /// <summary>Session checkboxes — passed into ApexV2Options.</summary>
    [ObservableProperty] private bool _tradeAsian;
    [ObservableProperty] private bool _tradeLondon = true;
    [ObservableProperty] private bool _tradeNewYork = true;
    [ObservableProperty] private bool _tradeLondonNy = true;

    /// <summary>Close bars on constant traded volume instead of a fixed time interval. When on, the
    /// selected candle interval still serves as the reference span for TTLs / distance scaling.</summary>
    [ObservableProperty] private bool _useVolumeBars;

    /// <summary>Contracts/shares per bar when <see cref="UseVolumeBars"/> is set (default 2000).</summary>
    [ObservableProperty] private int _volumeBarSize = 2_000;

    // ── Dashboard observables (updated in OnBarsUpdated) ────────────────────────────────────────

    /// <summary>Latest flat v2 snapshot. Bound by dashboard panel.</summary>
    [ObservableProperty] private ApexSnapshotV2? _latestSnapshot;

    // ── Composite diagnostics ────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _compositeText = "—";
    [ObservableProperty] private string _compositeDirectionText = "—";
    [ObservableProperty] private string _gcText = "—";
    [ObservableProperty] private string _costHurdleText = "—";
    [ObservableProperty] private bool _bootstrapMode;
    [ObservableProperty] private string _regimeText = "—";
    [ObservableProperty] private string _sessionPnlText = "—";

    // ── Feed quality / bootstrap badges ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _feedQualityText = "—";
    [ObservableProperty] private bool _isSyntheticFeed;   // true when FeedQuality != RealTape

    // ── Kyle / epsilon ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _kyleLambdaText = "—";
    [ObservableProperty] private string _epsilonCumText = "—";
    [ObservableProperty] private string _epsilonCumZText = "—";

    // ── Control / wedge geometry ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _controlRhoText = "—";
    [ObservableProperty] private string _controlVelocityText = "—";
    [ObservableProperty] private string _wedgeWidthText = "—";
    [ObservableProperty] private string _wedgeVelocityText = "—";
    [ObservableProperty] private string _valueDeviationZText = "—";

    // ── Line-fit triple ──────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _buyLineSlopeText = "—";
    [ObservableProperty] private string _buyLineR2Text = "—";
    [ObservableProperty] private string _sellLineSlopeText = "—";
    [ObservableProperty] private string _sellLineR2Text = "—";
    [ObservableProperty] private string _pocLineSlopeText = "—";
    [ObservableProperty] private string _pocLineR2Text = "—";

    // ── Weight vector ────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<WeightRow> SignalWeights { get; } = new();

    // ── Per-signal list ──────────────────────────────────────────────────────────────────────────
    //   Exposed as a flat list of ApexSignalState for an ItemsControl row binder.

    public ObservableCollection<ApexSignalState> SignalStates { get; } = new();

    // ── Footprint overlay toggles (predicted nodes + regression lines on the cluster) ─────────────

    /// <summary>Draw the buy/sell/POC regression lines over the footprint cluster.</summary>
    [ObservableProperty] private bool _showRegressionLines = true;

    /// <summary>Draw the Kalman-forecast predicted POC nodes (forecast region) over the cluster.</summary>
    [ObservableProperty] private bool _showPredictedNodes = true;

    // ── Paper trading (blotter + summary; engine simulates the OMS, armed via Run) ─────────────────

    /// <summary>One formatted row in the paper-trade blotter.</summary>
    public sealed record PaperTradeRow(
        string ExitTimeText, string SideText, long Quantity,
        string EntryText, string ExitText, double Pnl, string PnlText, string ExitReason);

    /// <summary>Completed paper trades, newest first.</summary>
    public ObservableCollection<PaperTradeRow> PaperTrades { get; } = new();

    [ObservableProperty] private string _paperPnlText = "0.00";
    [ObservableProperty] private string _paperTradeCountText = "0";
    [ObservableProperty] private string _paperWinRateText = "—";
    [ObservableProperty] private string _paperOpenText = "flat";
    [ObservableProperty] private bool _paperArmed;

    // ── Engine reference ─────────────────────────────────────────────────────────────────────────

    private Engine.ApexScalperStrategy? _engine;

    /// <summary>Newest paper-trade record already mirrored into <see cref="PaperTrades"/>. Used to detect
    /// fresh fills by identity rather than by count — the engine caps its trade log at a rolling window,
    /// so once that cap is hit the count stops changing and a count-based check would freeze the blotter
    /// on a long (multi-day) run.</summary>
    private ApexTradeRecord? _lastSyncedTrade;

    /// <summary>Exposed for the window's footprint chart redraw.</summary>
    public Engine.ApexScalperStrategy? EngineStrategy => _engine;

    // ── Ctor ─────────────────────────────────────────────────────────────────────────────────────

    public SigmaIcFlowStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<SigmaIcFlowStrategyViewModel> logger)
        : base(
            strategyId: "sigma.ic.flow",
            strategyDisplayName: "Σ⁻¹·IC Order-Flow Optimizer",
            services, notifications, clock, routerFactory, logger)
    {
        SelectedCandleInterval = CandleIntervals.First(i => i.Label == "1m");

        // Arm/disarm paper trading the instant the Run toggle flips (not just on the next bar close).
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsAlgoRunning) && _engine is not null)
            {
                _engine.PaperTradingEnabled = IsAlgoRunning;
                PaperArmed = IsAlgoRunning;
            }
        };
    }

    // ── DataRequirement override — includes TradeTape so the base starts the trade pump ─────────

    protected override StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars |
        StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

    // ── Strategy construction ────────────────────────────────────────────────────────────────────

    protected override IBacktestStrategy BuildStrategy(Contract contract)
    {
        var options = new ApexV2Options
        {
            KellyFraction = KellyFraction,
            CompositeThreshold = BootstrapThreshold,
            TradeAsian = TradeAsian,
            TradeLondon = TradeLondon,
            TradeNewYork = TradeNewYork,
            TradeLondonNy = TradeLondonNy,
            TickSizeOverride = TickSizeOverrideParsed,
            BarMode = UseVolumeBars ? ApexBarMode.Volume : ApexBarMode.Time,
            VolumeBarSize = Math.Max(1, VolumeBarSize),
        };
        _engine = new Engine.ApexScalperStrategy(
            contract,
            options: options,
            candleInterval: SelectedCandleInterval?.Span,
            instrumentTick: FootprintTickSizeParsed);
        // Start disarmed in live mode — paper trades only accrue once the user presses Run.
        _engine.PaperTradingEnabled = IsAlgoRunning;
        return _engine;
    }

    // ── Warm-up ──────────────────────────────────────────────────────────────────────────────────

    protected override int WarmupBarCount => Math.Max(150, MaxChartCandles);

    protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
    {
        _engine?.SeedFromBars(bars);
        return Task.CompletedTask;
    }

    // ── Per-bar update ───────────────────────────────────────────────────────────────────────────

    protected override void OnBarsUpdated()
    {
        var snap = _engine?.Latest;
        LatestSnapshot = snap;
        if (snap is null) return;

        // Composite
        var dir = snap.CompositeDirection switch { > 0 => "LONG", < 0 => "SHORT", _ => "FLAT" };
        CompositeText = snap.Composite.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        CompositeDirectionText = dir;
        BootstrapMode = snap.BootstrapMode;
        RegimeText = snap.Regime;
        SessionPnlText = snap.SessionPnl.ToString("F2", CultureInfo.InvariantCulture);

        // g(C) vs cost hurdle — engine provides g(C) directly; cost hurdle = cond slippage
        // (spread + fees are engine-internal; we surface g(C) and conditional slippage as proxies).
        GcText = snap.CalibratedExpectedReturn.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        CostHurdleText = snap.ConditionalSlippage.ToString("F5", CultureInfo.InvariantCulture);

        // Feed quality badge
        FeedQualityText = snap.FeedQuality switch
        {
            FeedQuality.RealTape => "Real Tape",
            FeedQuality.SyntheticL1 => "Synthetic L1",
            _ => "None",
        };
        IsSyntheticFeed = snap.FeedQuality != FeedQuality.RealTape;

        // Kyle / epsilon
        KyleLambdaText = snap.KyleLambda.ToString("F6", CultureInfo.InvariantCulture);
        EpsilonCumText = snap.EpsilonCum.ToString("F4", CultureInfo.InvariantCulture);
        EpsilonCumZText = snap.EpsilonCumZ.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

        // Control / wedge
        ControlRhoText = snap.ControlCoordinate.ToString("F3", CultureInfo.InvariantCulture);
        ControlVelocityText = snap.ControlVelocity.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
        WedgeWidthText = snap.WedgeWidth.ToString("F4", CultureInfo.InvariantCulture);
        WedgeVelocityText = snap.WedgeWidthVelocity.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        ValueDeviationZText = snap.ValueDeviationZ.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

        // Line fits
        BuyLineSlopeText = snap.BuyLine.Slope.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        BuyLineR2Text = snap.BuyLine.RSquared.ToString("F3", CultureInfo.InvariantCulture);
        SellLineSlopeText = snap.SellLine.Slope.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        SellLineR2Text = snap.SellLine.RSquared.ToString("F3", CultureInfo.InvariantCulture);
        PocLineSlopeText = snap.PocLine.Slope.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        PocLineR2Text = snap.PocLine.RSquared.ToString("F3", CultureInfo.InvariantCulture);

        // Weight vector — rebuild only when weights change (reference comparison on the dict suffices
        // since BuildSnapshot creates a new Dictionary each time it runs, but we do a lightweight
        // count+first-entry check to avoid clearing/refilling on every tick-level live snap).
        RebuildWeights(snap.Weights);

        // Per-signal states — stable-order pass from the engine's canonical SignalNames array.
        RebuildSignalStates(snap.Signals);

        var validCount = snap.Signals.Count(s => s.IsValid);
        Log("Σ⁻¹·IC", $"C={snap.Composite:+0.00;-0.00} {dir} g(C)={snap.CalibratedExpectedReturn:+0.0000;-0.0000} " +
                    $"valid={validCount}/{snap.Signals.Count} regime={snap.Regime} " +
                    $"q={snap.FeedQuality} bootstrap={snap.BootstrapMode} " +
                    $"trade={(snap.TradeAllowed ? "ok" : "blocked")}");

        SyncPaperTrades(snap);
    }

    /// <summary>Mirrors the engine's simulated trade log + open position into the blotter and summary.
    /// Paper trading is armed by the Run toggle (<see cref="LiveSignalStrategyViewModelBase.IsAlgoRunning"/>):
    /// when disarmed the engine opens no new positions, so the blotter only fills once Run is on.</summary>
    private void SyncPaperTrades(ApexSnapshotV2 snap)
    {
        var engine = _engine;
        if (engine is null) return;

        // Tie paper-trade arming to the Run toggle (default-on engine flag for headless backtests).
        engine.PaperTradingEnabled = IsAlgoRunning;
        PaperArmed = IsAlgoRunning;

        var trades = engine.Trades;
        var newest = trades.Count > 0 ? trades[^1] : null;
        if (!ReferenceEquals(newest, _lastSyncedTrade))
        {
            // Count how many records are new since the last sync by walking back to the anchor. The
            // engine appends newest at the end and evicts the oldest from the front (rolling window),
            // so this is normally a tiny delta — far cheaper than clearing and refilling all ~1k rows.
            var newCount = 0;
            for (var i = trades.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(trades[i], _lastSyncedTrade)) break;
                newCount++;
            }

            if (newCount >= trades.Count)
            {
                // Anchor not found (first fill / fresh engine after Restart / evicted past the window)
                // → full rebuild, newest first.
                PaperTrades.Clear();
                for (var i = trades.Count - 1; i >= 0; i--) PaperTrades.Add(ToRow(trades[i]));
            }
            else
            {
                // Prepend just the new fills (newest first), then trim the tail to the engine's window
                // so the blotter can't grow past it over a long run.
                for (var i = trades.Count - newCount; i < trades.Count; i++)
                    PaperTrades.Insert(0, ToRow(trades[i]));
                while (PaperTrades.Count > trades.Count) PaperTrades.RemoveAt(PaperTrades.Count - 1);
            }

            _lastSyncedTrade = newest;
        }

        double total = 0; var wins = 0;
        foreach (var t in trades) { total += t.Pnl; if (t.Pnl > 0) wins++; }
        var inv = CultureInfo.InvariantCulture;
        PaperPnlText = total.ToString("+0.00;-0.00;0.00", inv);
        PaperTradeCountText = trades.Count.ToString(inv);
        PaperWinRateText = trades.Count > 0 ? ((double)wins / trades.Count).ToString("P0", inv) : "—";

        var pos = snap.Position;
        PaperOpenText = pos == 0
            ? "flat"
            : $"{(pos > 0 ? "LONG" : "SHORT")} {Math.Abs(pos)} @ {engine.OpenEntryPrice.ToString("F2", inv)} " +
              $"(stop {engine.OpenStopPrice.ToString("F2", inv)} / tgt {engine.OpenTargetPrice.ToString("F2", inv)})";
    }

    private static PaperTradeRow ToRow(ApexTradeRecord t)
    {
        var inv = CultureInfo.InvariantCulture;
        return new PaperTradeRow(
            t.ExitUtc.ToLocalTime().ToString("HH:mm:ss"),
            t.Direction > 0 ? "LONG" : "SHORT",
            t.Quantity,
            t.EntryPrice.ToString("F2", inv),
            t.ExitPrice.ToString("F2", inv),
            t.Pnl,
            t.Pnl.ToString("+0.00;-0.00;0.00", inv),
            t.ExitReason);
    }

    private void RebuildWeights(IReadOnlyDictionary<string, double> weights)
    {
        // Refresh if the count or any key changed; otherwise update values in place.
        if (SignalWeights.Count != weights.Count)
        {
            SignalWeights.Clear();
            foreach (var kv in weights.OrderByDescending(k => Math.Abs(k.Value)))
                SignalWeights.Add(new WeightRow(kv.Key, kv.Value));
            return;
        }
        // Same count: update existing rows by index (weights come in consistent order after warm-up).
        var ordered = weights.OrderByDescending(k => Math.Abs(k.Value)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i >= SignalWeights.Count) break;
            if (SignalWeights[i].SignalName != ordered[i].Key || Math.Abs(SignalWeights[i].Weight - ordered[i].Value) > 1e-9)
                SignalWeights[i] = new WeightRow(ordered[i].Key, ordered[i].Value);
        }
    }

    private void RebuildSignalStates(IReadOnlyList<ApexSignalState> signals)
    {
        // Sync in-place to avoid full teardown of bindings.
        for (var i = 0; i < signals.Count; i++)
        {
            if (i < SignalStates.Count)
            {
                if (!ReferenceEquals(SignalStates[i], signals[i]))
                    SignalStates[i] = signals[i];
            }
            else
            {
                SignalStates.Add(signals[i]);
            }
        }
        while (SignalStates.Count > signals.Count)
            SignalStates.RemoveAt(SignalStates.Count - 1);
    }

    // ── Validation ───────────────────────────────────────────────────────────────────────────────

    protected override string? ValidateSetup()
    {
        if (KellyFraction <= 0 || KellyFraction > 1) return "Kelly fraction must be in (0, 1].";
        if (BootstrapThreshold <= 0 || BootstrapThreshold > 3) return "Bootstrap threshold must be in (0, 3].";
        if (FootprintBarsVisible < 4 || FootprintBarsVisible > 24) return "Footprint bars must be in [4, 24].";
        if (MaxChartCandles < 10 || MaxChartCandles > 500) return "Max chart candles must be in [10, 500].";
        if (SelectedCandleInterval is null) return "Pick a candle interval.";
        if (!double.TryParse(FootprintTickSizeText, NumberStyles.Any, CultureInfo.InvariantCulture, out var tick) || tick <= 0)
            return "Footprint tick size must be a positive number (e.g. 0.25 or 0.0001).";
        if (TickSizeOverrideText.Trim() != "0" &&
            (!double.TryParse(TickSizeOverrideText, NumberStyles.Any, CultureInfo.InvariantCulture, out var ov) || ov < 0))
            return "Tick-size override must be 0 (auto) or a positive number.";
        if (!TradeAsian && !TradeLondon && !TradeNewYork && !TradeLondonNy)
            return "Enable at least one session.";
        if (UseVolumeBars && VolumeBarSize < 100)
            return "Volume bar size must be at least 100 contracts.";
        return null;
    }
}
