using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// View-model for the Backtest Studio. Runs the new <see cref="BacktestEngine"/> over a synthetic
/// feed (zero-setup), exposes the resulting report + metrics for the Report tab, and drives the
/// visual replay (a single playback <see cref="DispatcherTimer"/> walks a cursor across the recorded
/// bars; the view redraws once per frame). Owns a run CTS and the timer, so it is
/// <see cref="IDisposable"/> and tears both down on close.
/// </summary>
public sealed partial class BacktestStudioViewModel : ViewModelBase, IDisposable
{
    private readonly IStrategyKernelRegistry _registry;
    private readonly ILogger<BacktestStudioViewModel> _logger;
    private readonly DispatcherTimer _playback;
    private static readonly InstrumentId SynthInstrument = new(1);

    private CancellationTokenSource? _runCts;

    public BacktestStudioViewModel(IStrategyKernelRegistry registry, ILogger<BacktestStudioViewModel> logger)
    {
        _registry = registry;
        _logger = logger;

        Strategies = new ObservableCollection<StrategyKernelDescriptor>(registry.All);
        Parameters = new ObservableCollection<ParamRowViewModel>();
        Trades = new ObservableCollection<RoundTripTrade>();

        _playback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playback.Tick += OnPlaybackTick;

        SelectedStrategy = Strategies.FirstOrDefault();
    }

    public ObservableCollection<StrategyKernelDescriptor> Strategies { get; }
    public ObservableCollection<ParamRowViewModel> Parameters { get; }
    public ObservableCollection<RoundTripTrade> Trades { get; }

    /// <summary>The last completed report — read by the view to draw the equity curve.</summary>
    public BacktestReport? Report { get; private set; }

    [ObservableProperty]
    private StrategyKernelDescriptor? _selectedStrategy;

    [ObservableProperty] private double _startingCash = 100_000;
    [ObservableProperty] private int _syntheticTicks = 5_000;
    [ObservableProperty] private int _seed = 1;
    [ObservableProperty] private bool _recordVisual = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRunning))]
    private bool _isRunning;

    [ObservableProperty] private string? _status;

    // Report metrics (set after a run).
    [ObservableProperty] private double _netProfit;
    [ObservableProperty] private double _sharpe;
    [ObservableProperty] private double _maxDrawdown;
    [ObservableProperty] private double _winRate;
    [ObservableProperty] private double _profitFactor;
    [ObservableProperty] private int _tradeCount;

    // Visual replay.
    [ObservableProperty] private bool _hasVisual;
    [ObservableProperty] private int _barCount;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _playbackSpeed = 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentBarText))]
    private int _currentBar;

    public bool IsNotRunning => !IsRunning;
    public string CurrentBarText => $"{CurrentBar} / {BarCount}";

    /// <summary>Raised after a run so the view can draw the equity curve and reset the replay chart.</summary>
    public event EventHandler? ReportReady;

    /// <summary>Raised each replay frame (and on seek) so the view redraws candles up to <see cref="CurrentBar"/>.</summary>
    public event EventHandler? ReplayFrameChanged;

    partial void OnSelectedStrategyChanged(StrategyKernelDescriptor? value)
    {
        Parameters.Clear();
        if (value is null) return;
        foreach (var p in value.Schema.Parameters)
            Parameters.Add(new ParamRowViewModel(p));
    }

    partial void OnCurrentBarChanged(int value) => ReplayFrameChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning || SelectedStrategy is null) return;

        StopPlayback();
        IsRunning = true;
        Status = "Running…";
        Trades.Clear();
        Report = null;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var overrides = Parameters.ToDictionary(p => p.Name, p => p.Resolved);
            var descriptor = SelectedStrategy;
            var spec = new RunSpec(
                Universe: Universe.Single(new InstrumentSpec(SynthInstrument, Contract.UsStock("SYN"), 0.01, 1.0)),
                Data: new DataSpec(),
                StrategyId: descriptor.Id,
                Parameters: descriptor.Schema.Resolve(overrides),
                StartingCash: StartingCash,
                Visual: RecordVisual ? VisualRecording.On : VisualRecording.Off);

            var feed = new SyntheticMarketDataFeed(SynthInstrument, SyntheticTicks, Seed);
            var report = await Task.Run(() =>
                new BacktestEngine(feed).RunAsync(spec, descriptor.Create(), ct), ct);

            Report = report;
            foreach (var t in report.Trades) Trades.Add(t);

            NetProfit = report.Summary.NetProfit;
            Sharpe = report.Metrics.Sharpe;
            MaxDrawdown = report.Metrics.MaxDrawdown;
            WinRate = report.Metrics.WinRate;
            ProfitFactor = report.Metrics.ProfitFactor;
            TradeCount = report.Trades.Count;

            HasVisual = report.Visual is { Bars.Count: > 0 };
            BarCount = report.Visual?.Bars.Count ?? 0;
            CurrentBar = BarCount; // show the whole run; replay rewinds from here

            Status = $"Done. {report.Trades.Count} trades, P&L {report.Summary.NetProfit:C2}, " +
                     $"{report.Summary.EventsProcessed:N0} events in {report.Summary.EngineMilliseconds:F0} ms.";
            ReportReady?.Invoke(this, EventArgs.Empty);
            ReplayFrameChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest Studio run failed");
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasVisual) return;
        if (IsPlaying) { StopPlayback(); return; }
        if (CurrentBar >= BarCount) CurrentBar = 0; // restart if parked at the end
        IsPlaying = true;
        _playback.Start();
    }

    [RelayCommand]
    private void Rewind()
    {
        StopPlayback();
        CurrentBar = 0;
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        var next = CurrentBar + Math.Max(1, (int)PlaybackSpeed);
        if (next >= BarCount)
        {
            CurrentBar = BarCount;
            StopPlayback();
        }
        else
        {
            CurrentBar = next;
        }
    }

    private void StopPlayback()
    {
        _playback.Stop();
        IsPlaying = false;
    }

    public void Dispose()
    {
        _playback.Tick -= OnPlaybackTick;
        _playback.Stop();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }
}
