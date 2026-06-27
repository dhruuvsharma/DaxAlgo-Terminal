using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.Ai.MarketAnalyst;

/// <summary>
/// View-model for the AI Market Analyst dock pane. Fetches a window of recent bars from
/// <see cref="IMarketDataRepository"/>, ships them to <see cref="IAiAnalystClient"/>, and
/// surfaces the structured verdict for binding. Empty state binds against
/// <see cref="AnalystReport.Unavailable"/> so the pane renders cleanly under the Null
/// client.
/// </summary>
public sealed partial class AiAnalystViewModel : ViewModelBase
{
    private readonly IAiAnalystClient _analyst;
    private readonly IMarketDataRepository _repository;
    private readonly IBrokerSelector _selector;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<AiAnalystViewModel> _logger;
    private readonly IDisposable? _optionsSubscription;
    private CancellationTokenSource? _runCts;

    public AiAnalystViewModel(
        IAiAnalystClient analyst,
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<AiAnalystViewModel> logger)
    {
        _analyst = analyst;
        _repository = repository;
        _selector = selector;
        _options = options;
        _logger = logger;

        History = new ObservableCollection<AnalystReport>();
        Timeframes = new ObservableCollection<string>
        {
            "1m", "3m", "5m", "15m", "1h", "4h", "1d",
        };

        var o = _options.CurrentValue.AiAnalyst;
        BarCount = o.BarCount > 0 ? o.BarCount : 50;
        SelectedTimeframe = "1h";
        LatestReport = AnalystReport.Unavailable(
            analyst.IsAvailable
                ? "Click Analyze to fetch a fresh verdict."
                : "AI Analyst unavailable — enable in Settings → Notifications → AI Analyst.");

        // The Settings tab writes notifications.json with reloadOnChange:true, so when the user
        // ticks Enabled, IOptionsMonitor fires. Marshal back to the UI thread and re-notify
        // IsAvailable so the Analyze button binding re-evaluates without a tab reopen.
        // Marshal the IOptionsMonitor change callback to the UI thread via the portable hook
        // (was Application.Current.Dispatcher; now WPF-free so it runs on both heads).
        _optionsSubscription = _options.OnChange(_ => UiThread.RunAsync(NotifyAvailabilityChanged));
    }

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        if (LatestReport is { Decision: AiAnalystDecision.NoCall } && _analyst.IsAvailable)
            LatestReport = AnalystReport.Unavailable("Click Analyze to fetch a fresh verdict.");
    }

    public ObservableCollection<AnalystReport> History { get; }
    public ObservableCollection<string> Timeframes { get; }

    public bool IsAvailable => _analyst.IsAvailable;

    [ObservableProperty] private string _selectedSymbol = "ES";
    [ObservableProperty] private string _selectedTimeframe = "1h";
    [ObservableProperty] private int _barCount = 50;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private AnalystReport? _latestReport;

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (IsRunning) return;
        if (!_analyst.IsAvailable)
        {
            ErrorMessage = "AI Analyst is not configured. Open Settings → Notifications and enable it.";
            return;
        }

        IsRunning = true;
        ErrorMessage = null;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        try
        {
            var bars = await FetchBarsAsync(SelectedSymbol, SelectedTimeframe, BarCount, ct);
            if (bars.Count == 0)
            {
                ErrorMessage = "No bars returned from the active broker.";
                LatestReport = AnalystReport.Unavailable("No bars returned from the active broker.");
                return;
            }

            var o = _options.CurrentValue.AiAnalyst;
            var request = new AnalystRequest(
                Symbol: SelectedSymbol,
                Timeframe: SelectedTimeframe,
                BarCount: bars.Count,
                Provider: o.Provider,
                Model: o.Model,
                VisionModel: o.VisionModel,
                Bars: bars);

            var report = await _analyst.RunAsync(request, ct);
            LatestReport = report;
            History.Insert(0, report);
            while (History.Count > 20) History.RemoveAt(History.Count - 1);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Analyst run failed");
            ErrorMessage = $"Failed: {ex.Message}";
            LatestReport = AnalystReport.Unavailable($"Failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    public void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    public void ClearHistory() => History.Clear();

    private async Task<IReadOnlyList<AnalystBar>> FetchBarsAsync(
        string symbol, string timeframe, int barCount, CancellationToken ct)
    {
        var contract = Contract.UsStock(symbol);
        var (size, perBar) = MapTimeframe(timeframe);
        var duration = TimeSpan.FromSeconds(perBar.TotalSeconds * Math.Max(barCount, 1) * 1.2);

        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        var broker = connected[0];
        var bars = await _repository.GetHistoricalBarsAsync(contract, broker, size, duration, ct);
        return bars.Count <= barCount
            ? bars.Select(ToAnalystBar).ToArray()
            : bars.Skip(bars.Count - barCount).Select(ToAnalystBar).ToArray();
    }

    private static AnalystBar ToAnalystBar(Bar b) =>
        new(b.TimestampUtc, b.Open, b.High, b.Low, b.Close, b.Volume);

    private static (BarSize size, TimeSpan perBar) MapTimeframe(string tf) => tf switch
    {
        "1m"  => (BarSize.OneMinute, TimeSpan.FromMinutes(1)),
        "3m"  => (BarSize.ThreeMinutes, TimeSpan.FromMinutes(3)),
        "5m"  => (BarSize.FiveMinutes, TimeSpan.FromMinutes(5)),
        "15m" => (BarSize.FifteenMinutes, TimeSpan.FromMinutes(15)),
        "1h"  => (BarSize.OneHour, TimeSpan.FromHours(1)),
        // 4h and 1d both fall back to the nearest supported BarSize on the broker side;
        // the Python analyst doesn't care about the underlying bar grain as long as the
        // timestamps are honest.
        "4h"  => (BarSize.OneHour, TimeSpan.FromHours(1)),
        "1d"  => (BarSize.OneDay, TimeSpan.FromDays(1)),
        _     => (BarSize.OneHour, TimeSpan.FromHours(1)),
    };
}
