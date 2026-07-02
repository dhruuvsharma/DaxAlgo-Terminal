using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant.Surfaces;
using TradingTerminal.UI;

namespace TradingTerminal.SurfaceLab;

/// <summary>
/// View-model for the 3D Surface Lab (Charts menu). The surface-mode selector re-targets the
/// four <see cref="AxisConfigViewModel"/>s (X/Y variables, Z height metric, W color metric —
/// each with optional custom formula); Generate pulls historical bars and builds the grid off
/// the UI thread via <see cref="SurfaceGridBuilder"/>. The view renders on
/// <see cref="SurfaceUpdated"/> (Helix mesh + analytics) and <see cref="SliceChanged"/>
/// (cutting planes + 2D slice charts only — cheap, safe to fire per slider tick).
/// Purely on-demand — no live feeds, no timers; Dispose cancels any in-flight build.
/// </summary>
public sealed partial class SurfaceLabViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<SurfaceLabViewModel> _logger;
    private CancellationTokenSource? _runCts;

    public const int MaxInstrumentsDisplayed = 500;

    private static readonly IReadOnlyList<TimeframeOption> AllTimeframes = new TimeframeOption[]
    {
        new("1m",  BarSize.OneMinute),
        new("5m",  BarSize.FiveMinutes),
        new("15m", BarSize.FifteenMinutes),
        new("1h",  BarSize.OneHour),
        new("1d",  BarSize.OneDay),
    };

    public SurfaceLabViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        ILogger<SurfaceLabViewModel> logger)
    {
        _repository = repository;
        _selector = selector;
        _logger = logger;

        Modes = new ObservableCollection<SurfaceModeOption>
        {
            new("Parameter Optimization", SurfaceMode.ParameterOptimization,
                "Backtest landscape: sweep two strategy parameters; height = performance, color = risk."),
            new("Temporal Aggregation", SurfaceMode.TemporalAggregation,
                "Seasonality surface: bucket returns by two calendar dimensions (hour × weekday, …)."),
            new("Statistical / Cross-Sectional", SurfaceMode.CrossSectional,
                "Conditional returns: bucket by prior return / volatility / volume / lag; height = next-period statistic."),
        };

        XAxis = new AxisConfigViewModel(SurfaceAxisRole.X, "X axis");
        YAxis = new AxisConfigViewModel(SurfaceAxisRole.Y, "Y axis");
        ZAxis = new AxisConfigViewModel(SurfaceAxisRole.Z, "Z axis (height)");
        WAxis = new AxisConfigViewModel(SurfaceAxisRole.Color, "Color (W)");

        Timeframes = new ObservableCollection<TimeframeOption>(AllTimeframes);
        SelectedTimeframe = Timeframes.First(t => t.BarSize == BarSize.OneHour);

        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        SelectedMode = Modes[0];
        _ = LoadInstrumentsAsync();
    }

    // ── Configuration surface ─────────────────────────────────────────────────────────────────

    public ObservableCollection<SurfaceModeOption> Modes { get; }
    public AxisConfigViewModel XAxis { get; }
    public AxisConfigViewModel YAxis { get; }
    public AxisConfigViewModel ZAxis { get; }
    public AxisConfigViewModel WAxis { get; }

    public ObservableCollection<TimeframeOption> Timeframes { get; }
    public ObservableCollection<SignalInstrument> Instruments { get; private set; }
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    [ObservableProperty] private SurfaceModeOption? _selectedMode;
    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private TimeframeOption? _selectedTimeframe;
    [ObservableProperty] private int _barCount = 2000;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _runStatus = "Configure the axes and press Generate Surface.";

    // ── Quant display toggles ─────────────────────────────────────────────────────────────────

    /// <summary>Places a red pin + value label on the surface's absolute maximum.</summary>
    [ObservableProperty] private bool _showPeakMarker = true;

    /// <summary>Colors the surface by neighbor-gradient robustness (green plateau = robust,
    /// red spike = overfit) instead of by the W metric.</summary>
    [ObservableProperty] private bool _robustnessColorMode;

    /// <summary>Vertical exaggeration of the Z axis (render-only).</summary>
    [ObservableProperty] private double _heightScale = 1.0;

    // ── Result + analytics ────────────────────────────────────────────────────────────────────

    /// <summary>The computed surface. Null until the first successful Generate.</summary>
    public SurfaceGridResult? Result { get; private set; }

    public bool HasResult => Result is not null;

    [ObservableProperty] private string _globalMaxText = "—";
    [ObservableProperty] private string _globalMinText = "—";
    [ObservableProperty] private string _peakLocationText = "—";
    [ObservableProperty] private string _cellCountText = "—";

    // ── Interactive slicing ───────────────────────────────────────────────────────────────────

    [ObservableProperty] private int _sliceXIndex;
    [ObservableProperty] private int _sliceYIndex;
    [ObservableProperty] private int _sliceXMax;
    [ObservableProperty] private int _sliceYMax;
    [ObservableProperty] private string _sliceXLabel = "—";
    [ObservableProperty] private string _sliceYLabel = "—";

    /// <summary>Raised after a successful Generate — full 3D + analytics redraw.</summary>
    public event EventHandler? SurfaceUpdated;

    /// <summary>Raised when a slice slider moves — the view redraws only the cutting planes
    /// and the 2D slice charts, never the mesh.</summary>
    public event EventHandler? SliceChanged;

    partial void OnSelectedModeChanged(SurfaceModeOption? value)
    {
        if (value is null) return;
        // Different default picks for X vs Y so the initial grid is never degenerate
        // (fastma × slowma, hour × weekday, return-bin × vol-bucket).
        XAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.X), preferredIndex: 0);
        YAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Y), preferredIndex: 1);
        ZAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Z), preferredIndex: 0);
        WAxis.SetOptions(SurfaceAxisCatalog.OptionsFor(value.Mode, SurfaceAxisRole.Color),
            preferredIndex: value.Mode == SurfaceMode.ParameterOptimization ? 5 /* maxdd */ : 2 /* stdret */);
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();
    partial void OnShowPeakMarkerChanged(bool value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    partial void OnRobustnessColorModeChanged(bool value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    partial void OnHeightScaleChanged(double value) => SurfaceUpdated?.Invoke(this, EventArgs.Empty);

    partial void OnSliceXIndexChanged(int value)
    {
        SliceXLabel = Result is { } r && value >= 0 && value < r.XLabels.Length ? r.XLabels[value] : "—";
        SliceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSliceYIndexChanged(int value)
    {
        SliceYLabel = Result is { } r && value >= 0 && value < r.YLabels.Length ? r.YLabels[value] : "—";
        SliceChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Generate ──────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task GenerateAsync()
    {
        if (IsRunning) return;
        if (SelectedMode is null) { ErrorMessage = "Pick a surface mode."; return; }
        if (SelectedInstrument is null) { ErrorMessage = "Pick an instrument."; return; }
        if (SelectedTimeframe is null) { ErrorMessage = "Pick a timeframe."; return; }
        if (BarCount < 200) { ErrorMessage = "Need at least 200 bars for meaningful statistics."; return; }

        var xSpec = XAxis.ToSpec(out var err);
        if (xSpec is null) { ErrorMessage = err; return; }
        var ySpec = YAxis.ToSpec(out err);
        if (ySpec is null) { ErrorMessage = err; return; }
        var zSpec = ZAxis.ToSpec(out err);
        if (zSpec is null) { ErrorMessage = err; return; }
        var wSpec = WAxis.ToSpec(out err);
        if (wSpec is null) { ErrorMessage = err; return; }

        ErrorMessage = null;
        IsRunning = true;
        RunStatus = "Loading bars…";
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var broker = ResolveBroker(SelectedInstrument);
            var duration = EstimateDuration(SelectedTimeframe.BarSize, BarCount);
            var bars = await _repository.GetHistoricalBarsAsync(
                SelectedInstrument.Contract, broker, SelectedTimeframe.BarSize, duration, ct);

            if (bars.Count < 200)
            {
                ErrorMessage = $"Only {bars.Count} bars returned; need at least 200.";
                return;
            }

            RunStatus = "Computing surface…";
            var request = new SurfaceRequest(
                SelectedMode.Mode, xSpec!, ySpec!, zSpec!, wSpec!,
                SurfaceGridBuilder.EstimatePeriodsPerYear(bars));

            var result = await Task.Run(() => SurfaceGridBuilder.Build(bars, request, ct), ct);
            Apply(result, bars.Count);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Surface Lab generation failed");
            ErrorMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    private void Apply(SurfaceGridResult result, int barCount)
    {
        Result = result;
        OnPropertyChanged(nameof(HasResult));

        var max = SurfaceGridAnalysis.FindMax(result.Z);
        var min = SurfaceGridAnalysis.FindMin(result.Z);
        GlobalMaxText = max.IsValid
            ? $"{SurfaceAxisFormats.Format(max.Value, result.ZFormat)}  @ {result.XName} {result.XLabels[max.Col]} · {result.YName} {result.YLabels[max.Row]}"
            : "—";
        GlobalMinText = min.IsValid
            ? $"{SurfaceAxisFormats.Format(min.Value, result.ZFormat)}  @ {result.XName} {result.XLabels[min.Col]} · {result.YName} {result.YLabels[min.Row]}"
            : "—";
        PeakLocationText = max.IsValid
            ? $"{result.XName} = {result.XLabels[max.Col]},  {result.YName} = {result.YLabels[max.Row]},  {result.ZName} = {SurfaceAxisFormats.Format(max.Value, result.ZFormat)}"
            : "—";
        CellCountText = $"{result.Columns} × {result.Rows} cells · {barCount} bars";
        RunStatus = $"{result.ZName} over {result.XName} × {result.YName} · {CellCountText}";

        // Park the slice cursors on the peak so the 2D viewer opens at the interesting spot.
        SliceXMax = result.Columns - 1;
        SliceYMax = result.Rows - 1;
        var newX = Math.Clamp(max.IsValid ? max.Col : result.Columns / 2, 0, SliceXMax);
        var newY = Math.Clamp(max.IsValid ? max.Row : result.Rows / 2, 0, SliceYMax);
        SliceXIndex = newX;
        SliceYIndex = newY;
        // Refresh labels + slice charts even when the indices didn't move (the data did).
        SliceXLabel = result.XLabels[newX];
        SliceYLabel = result.YLabels[newY];
        SliceChanged?.Invoke(this, EventArgs.Empty);

        SurfaceUpdated?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }

    // ── Shared picker plumbing (each tool owns its copy — independent projects) ─────────────

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    private static TimeSpan EstimateDuration(BarSize size, int barCount) => size switch
    {
        BarSize.OneMinute      => TimeSpan.FromMinutes(barCount * 1.5),
        BarSize.FiveMinutes    => TimeSpan.FromMinutes(barCount * 5 * 1.5),
        BarSize.FifteenMinutes => TimeSpan.FromMinutes(barCount * 15 * 1.5),
        BarSize.OneHour        => TimeSpan.FromHours(barCount * 1.5),
        BarSize.OneDay         => TimeSpan.FromDays(barCount * 1.5),
        _                      => TimeSpan.FromDays(7),
    };

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            AllInstruments = list.Select(i => new SignalInstrument(
                i.DisplayName, i.Category, i.Contract, i.Broker)).ToList();
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Surface Lab: broker universe load failed, using static catalog");
        }
    }

    private void ApplyInstrumentFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = AllInstruments;
        if (term.Length > 0)
            query = AllInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        var shown = query.Take(MaxInstrumentsDisplayed).ToList();

        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments = new ObservableCollection<SignalInstrument>(shown);
        OnPropertyChanged(nameof(Instruments));
        SelectedInstrument = keep is not null && Instruments.Contains(keep) ? keep : Instruments.FirstOrDefault();
    }
}

/// <summary>Surface-mode dropdown row.</summary>
public sealed record SurfaceModeOption(string Label, SurfaceMode Mode, string Description)
{
    public override string ToString() => Label;
}

/// <summary>Bar-size dropdown row. Each tool owns its own copy so the panels stay independent projects.</summary>
public sealed record TimeframeOption(string Label, BarSize BarSize)
{
    public override string ToString() => Label;
}
