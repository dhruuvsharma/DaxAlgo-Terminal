using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Apex;
using TradingTerminal.Core.Time;
using TradingTerminal.UI;
using Engine = TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

/// <summary>
/// A single row in the weight-vector table shown on the dashboard. Name → estimated Σ⁻¹·IC weight.
/// </summary>
public sealed record WeightRow(string SignalName, double Weight);

/// <summary>
/// Live signal-mode VM for the APEX v2 microstructure scalper. Instantiates the engine-side
/// <see cref="Engine.ApexScalperStrategy"/> inside <see cref="BuildStrategy"/> and projects
/// <see cref="Engine.ApexScalperStrategy.Latest"/> (<see cref="ApexSnapshotV2"/>) onto
/// observable properties consumed by the dashboard.
///
/// <para>No signal math lives here. The VM is a thin projection layer: it reads snapshot fields,
/// formats them for display, and exposes observable collections. All business logic is in the
/// engine.</para>
/// </summary>
public sealed partial class ApexScalperStrategyViewModel : LiveSignalStrategyViewModelBase
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
        new CandleIntervalOption("15s", TimeSpan.FromSeconds(15)),
        new CandleIntervalOption("30s", TimeSpan.FromSeconds(30)),
        new CandleIntervalOption("1m",  TimeSpan.FromMinutes(1)),
        new CandleIntervalOption("5m",  TimeSpan.FromMinutes(5)),
    });

    [ObservableProperty] private CandleIntervalOption? _selectedCandleInterval;

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

    // ── Engine reference ─────────────────────────────────────────────────────────────────────────

    private Engine.ApexScalperStrategy? _engine;

    /// <summary>Exposed for the window's footprint chart redraw.</summary>
    public Engine.ApexScalperStrategy? EngineStrategy => _engine;

    // ── Ctor ─────────────────────────────────────────────────────────────────────────────────────

    public ApexScalperStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<ApexScalperStrategyViewModel> logger)
        : base(
            strategyId: "apex.scalper",
            strategyDisplayName: "APEX microstructure scalper v2",
            services, notifications, clock, routerFactory, logger)
    {
        SelectedCandleInterval = CandleIntervals.First(i => i.Label == "1m");
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
        };
        _engine = new Engine.ApexScalperStrategy(
            contract,
            options: options,
            candleInterval: SelectedCandleInterval?.Span,
            instrumentTick: FootprintTickSizeParsed);
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
        Log("APEX", $"C={snap.Composite:+0.00;-0.00} {dir} g(C)={snap.CalibratedExpectedReturn:+0.0000;-0.0000} " +
                    $"valid={validCount}/{snap.Signals.Count} regime={snap.Regime} " +
                    $"q={snap.FeedQuality} bootstrap={snap.BootstrapMode} " +
                    $"trade={(snap.TradeAllowed ? "ok" : "blocked")}");
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
        return null;
    }
}
