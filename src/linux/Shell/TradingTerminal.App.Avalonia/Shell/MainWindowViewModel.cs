using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia.Shell;

/// <summary>
/// Root VM for the cross-platform shell — the Avalonia mirror of the WPF
/// <c>TradingTerminal.App.MainWindowViewModel</c>. Same surface (strategy catalog over
/// <see cref="IStrategyFactory"/>, aggregate connection state, UTC + local clocks, market-session
/// badges, the universal Activity Log with a live filter, and the header API meter) so the shell
/// reads identically to the WPF terminal. Strategy windows are opened through the factory by the
/// view's code-behind; the shell never names a concrete strategy.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IBrokerSelector _brokerSelector;
    private readonly DispatcherTimer _clockTimer;

    public MainWindowViewModel(
        IStrategyFactory factory,
        IBrokerSelector brokerSelector,
        BrokerApiMeterViewModel apiMeter,
        InMemoryLogSink activityLog)
    {
        _brokerSelector = brokerSelector;
        ApiMeter = apiMeter;
        ActivityLog = activityLog;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        VisibleLog = new ObservableCollection<LogEntry>(activityLog.Entries);
        activityLog.Entries.CollectionChanged += OnLogEntriesChanged;

        // 1 Hz clock + market-session flags (approximate; no DST/holiday calendar — same as WPF).
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _clockTimer.Start();
        UpdateClocks();

        RefreshAggregateState();
        _brokerSelector.StateChanged += OnBrokerStateChanged;

        activityLog.Append("Avalonia", "INFO", "Cross-platform shell started on the portable core (DI).");
        activityLog.Append("Avalonia", "INFO", $"{Strategies.Count} strategies loaded from the plug-in factory.");
    }

    /// <summary>Design-time ctor — empty graph so the previewer has something to render.</summary>
    public MainWindowViewModel()
    {
        _brokerSelector = null!;
        _clockTimer = null!;
        ActivityLog = new InMemoryLogSink();
        Strategies = new ObservableCollection<ITradingStrategy>();
        VisibleLog = new ObservableCollection<LogEntry>();
    }

    // ── Strategy catalog ────────────────────────────────────────────────────────────────────────

    /// <summary>The live strategy catalog — <see cref="IStrategyFactory.All"/> (ITradingStrategy),
    /// the same source the WPF shell binds to. Cards render the data-requirement + classification
    /// pills via the ported converters.</summary>
    public ObservableCollection<ITradingStrategy> Strategies { get; }

    [ObservableProperty] private ITradingStrategy? _selectedStrategy;

    /// <summary>Description shown in the details panel for the selected strategy.</summary>
    public string SelectedDetails => SelectedStrategy?.Description ?? "Select a strategy to see its description.";

    partial void OnSelectedStrategyChanged(ITradingStrategy? value) => OnPropertyChanged(nameof(SelectedDetails));

    // ── Header API meter ────────────────────────────────────────────────────────────────────────

    public BrokerApiMeterViewModel? ApiMeter { get; }

    // ── Universal Activity Log (filtered) ─────────────────────────────────────────────────────────

    public InMemoryLogSink ActivityLog { get; }

    /// <summary>Filtered view of the activity log shown in the bottom drawer.</summary>
    public ObservableCollection<LogEntry> VisibleLog { get; }

    [ObservableProperty] private string _logFilter = string.Empty;

    [ObservableProperty] private bool _isLogVisible;

    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Append new entries that match the filter; full rebuild on resets/removes (capacity trim).
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (LogEntry entry in e.NewItems)
                if (Matches(entry)) VisibleLog.Add(entry);
        }
        else
        {
            RebuildVisibleLog();
        }
    }

    private void RebuildVisibleLog()
    {
        VisibleLog.Clear();
        foreach (var entry in ActivityLog.Entries)
            if (Matches(entry)) VisibleLog.Add(entry);
    }

    private bool Matches(LogEntry e)
    {
        var f = LogFilter?.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return e.Source.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Level.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // ── Aggregate connection state + market sessions ──────────────────────────────────────────────

    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.Disconnected;
    [ObservableProperty] private string _currentTime = DateTime.Now.ToString("HH:mm:ss");
    [ObservableProperty] private string _currentTimeUtc = DateTime.UtcNow.ToString("HH:mm:ss");
    [ObservableProperty] private bool _nyseOpen;
    [ObservableProperty] private bool _lseOpen;

    public bool IsDisconnected => ConnectionState is not ConnectionState.Connected;
    public string DisconnectBannerText => "Disconnected — connect a broker to resume";
    public int ConnectedBrokerCount => _brokerSelector?.Connected.Count ?? 0;

    partial void OnConnectionStateChanged(ConnectionState value) => OnPropertyChanged(nameof(IsDisconnected));

    public string RuntimeInfo =>
        $"{RuntimeInformation.OSDescription}  ·  {RuntimeInformation.FrameworkDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    private void OnBrokerStateChanged(object? sender, BrokerStateChangedEventArgs e) =>
        Dispatcher.UIThread.Post(RefreshAggregateState);

    private void RefreshAggregateState()
    {
        var states = _brokerSelector.AvailableKinds.Select(k => _brokerSelector.CurrentStateOf(k)).ToList();
        ConnectionState =
            states.Any(s => s == ConnectionState.Connected) ? ConnectionState.Connected
            : states.Any(s => s == ConnectionState.Reconnecting) ? ConnectionState.Reconnecting
            : states.Any(s => s == ConnectionState.Connecting) ? ConnectionState.Connecting
            : states.Any(s => s == ConnectionState.Failed) ? ConnectionState.Failed
            : ConnectionState.Disconnected;
        OnPropertyChanged(nameof(ConnectedBrokerCount));
    }

    /// <summary>File → Reconnect: re-arm every available broker's connect loop (mirrors the WPF
    /// ReconnectAsync). Failures are swallowed per broker so one bad broker can't abort the rest.</summary>
    public async Task ReconnectAllAsync()
    {
        if (_brokerSelector is null) return;
        foreach (var kind in _brokerSelector.AvailableKinds)
        {
            try { await _brokerSelector.ConnectAsync(kind); }
            catch { /* observed via StateChanged; don't abort the loop */ }
        }
    }

    private void UpdateClocks()
    {
        CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        var utc = DateTime.UtcNow;
        CurrentTimeUtc = utc.ToString("HH:mm:ss");
        NyseOpen = IsSessionOpen(utc, 14, 30, 21, 0);  // ~09:30–16:00 ET
        LseOpen = IsSessionOpen(utc, 8, 0, 16, 30);     // ~08:00–16:30 London
    }

    private static bool IsSessionOpen(DateTime utc, int startH, int startM, int endH, int endM)
    {
        if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var minutes = utc.Hour * 60 + utc.Minute;
        return minutes >= startH * 60 + startM && minutes < endH * 60 + endM;
    }

    public void Dispose()
    {
        _clockTimer?.Stop();
        if (_brokerSelector is not null) _brokerSelector.StateChanged -= OnBrokerStateChanged;
        ActivityLog.Entries.CollectionChanged -= OnLogEntriesChanged;
        ApiMeter?.Dispose();
    }
}
