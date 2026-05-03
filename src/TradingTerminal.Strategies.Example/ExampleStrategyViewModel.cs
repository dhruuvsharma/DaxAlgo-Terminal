using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.Example;

public sealed partial class ExampleStrategyViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 200;

    private readonly IMarketDataRepository _repository;
    private readonly ILogger<ExampleStrategyViewModel> _logger;
    private CancellationTokenSource? _streamCts;

    public ExampleStrategyViewModel(IMarketDataRepository repository, ILogger<ExampleStrategyViewModel> logger)
    {
        _repository = repository;
        _logger = logger;

        TimeframeOptions = new[] { "1m", "3m", "5m", "15m", "1h", "1D" };
        Symbol = "NVDA";
        SelectedTimeframe = "3m";
        Bars = new ObservableCollection<Bar>();
    }

    public IReadOnlyList<string> TimeframeOptions { get; }

    public ObservableCollection<Bar> Bars { get; }

    [ObservableProperty]
    private string _symbol = "NVDA";

    [ObservableProperty]
    private string _selectedTimeframe = "3m";

    [ObservableProperty]
    private string _status = "Idle";

    [ObservableProperty]
    private double? _lastPrice;

    /// <summary>True while a backfill+subscribe pipeline is running.</summary>
    [ObservableProperty]
    private bool _isStreaming;

    /// <summary>Raised whenever new bars are appended (so the view can refresh the chart).</summary>
    public event EventHandler? BarsChanged;

    [RelayCommand]
    private async Task ApplyAsync()
    {
        await StopStreamAsync();
        await StartStreamAsync(CancellationToken.None);
    }

    /// <summary>Start the backfill + streaming pipeline. Idempotent.</summary>
    public async Task StartStreamAsync(CancellationToken ct)
    {
        if (IsStreaming) return;

        var contract = Contract.UsStock(Symbol.Trim().ToUpperInvariant());
        var size = BarSizeExtensions.FromDisplayString(SelectedTimeframe);

        Status = $"Loading historical {Symbol} {SelectedTimeframe}...";
        Bars.Clear();
        BarsChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var historical = await _repository.GetHistoricalBarsAsync(contract, size,
                TimeSpan.FromDays(1), ct);
            foreach (var b in historical.TakeLast(MaxBarsRetained))
                Bars.Add(b);
            LastPrice = Bars.LastOrDefault()?.Close;
            BarsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical backfill failed");
            Status = $"Historical fetch failed: {ex.Message}";
            return;
        }

        _streamCts = new CancellationTokenSource();
        var streamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _streamCts.Token).Token;

        IsStreaming = true;
        Status = $"Streaming {Symbol} {SelectedTimeframe}";

        _ = RunStreamAsync(contract, size, streamCt);
    }

    private async Task RunStreamAsync(Contract contract, BarSize size, CancellationToken ct)
    {
        try
        {
            await foreach (var bar in _repository.SubscribeBarsAsync(contract, size, ct))
            {
                AppendBar(bar);
                BarsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Streaming pipeline ended");
            Status = $"Stream stopped: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    /// <summary>Test seam: append a bar exactly the way the streaming pipeline would.</summary>
    public void AppendBar(Bar bar)
    {
        Bars.Add(bar);
        while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
        LastPrice = bar.Close;
    }

    public async Task StopStreamAsync()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        IsStreaming = false;
        Status = "Stopped";
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }
}
