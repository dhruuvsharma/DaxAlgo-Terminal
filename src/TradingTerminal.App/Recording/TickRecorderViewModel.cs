using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.App.Recording;

/// <summary>
/// Live tick recorder. Subscribes to <see cref="IMarketDataRepository.SubscribeTicksAsync"/>
/// for one contract via the active broker, streams to a parquet file via
/// <see cref="ParquetTickWriter"/>. Run for hours/days to build real intraday tape that
/// the backtest engine can replay with full microstructure fidelity — the same way real
/// quant desks build their proprietary tick archives.
///
/// L1 only today (parquet schema is L1). L2 recording would need a separate format /
/// writer for variable-size depth snapshots — out of scope for v1.
/// </summary>
public sealed partial class TickRecorderViewModel : ViewModelBase, IDisposable
{
    private readonly IMarketDataRepository _repository;
    private readonly ILogger<TickRecorderViewModel> _logger;
    private CancellationTokenSource? _streamCts;
    private ParquetTickWriter? _writer;
    private DateTime _startedAtUtc;

    public TickRecorderViewModel(
        IMarketDataRepository repository,
        ILogger<TickRecorderViewModel> logger)
    {
        _repository = repository;
        _logger = logger;
        Instruments = SignalInstrumentCatalog.All;
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "EUR")
                             ?? Instruments[0];
        var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DaxAlgo Terminal", "recordings");
        OutputPath = Path.Combine(defaultDir, $"ticks-{DateTime.UtcNow:yyyyMMdd-HHmm}.parquet");
    }

    public IReadOnlyList<SignalInstrument> Instruments { get; }
    public ObservableCollection<RecorderTickPreview> RecentTicks { get; } = new();
    public const int PreviewSize = 30;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private long _ticksWritten;
    [ObservableProperty] private string _status = "Pick an instrument and press Start.";
    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private string _elapsed = "00:00:00";
    [ObservableProperty] private string? _validationError;

    [RelayCommand]
    private void BrowseOutput()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Parquet files (*.parquet)|*.parquet|All files (*.*)|*.*",
            DefaultExt = ".parquet",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath),
        };
        if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument first."; return; }
        if (string.IsNullOrWhiteSpace(OutputPath)) { ValidationError = "Pick an output path."; return; }
        if (IsRecording) return;

        try
        {
            var dir = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new ParquetTickWriter(OutputPath);
        }
        catch (Exception ex)
        {
            ValidationError = $"Couldn't create output file: {ex.Message}";
            _logger.LogError(ex, "Recorder file open failed");
            return;
        }

        RecentTicks.Clear();
        TicksWritten = 0;
        _startedAtUtc = DateTime.UtcNow;
        _streamCts = new CancellationTokenSource();
        IsRecording = true;
        Status = $"Recording {SelectedInstrument.DisplayName} → {Path.GetFileName(OutputPath)}";

        _ = RunStreamAsync(SelectedInstrument.Contract, _streamCts.Token);
        _ = TickElapsedAsync(_streamCts.Token);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRecording) return;
        _streamCts?.Cancel();
        IsRecording = false;
        Status = $"Stopped. Wrote {TicksWritten} ticks to {Path.GetFileName(OutputPath)}.";
        if (_writer is not null) { await _writer.DisposeAsync(); _writer = null; }
        _streamCts?.Dispose(); _streamCts = null;
    }

    private async Task RunStreamAsync(Contract contract, CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _repository.SubscribeTicksAsync(contract, ct))
            {
                if (_writer is null) break;
                await _writer.WriteAsync(tick);
                TicksWritten++;
                LastBid = tick.Bid;
                LastAsk = tick.Ask;

                RecentTicks.Insert(0, new RecorderTickPreview(tick.TimestampUtc, tick.Bid, tick.Ask, tick.BidSize, tick.AskSize));
                while (RecentTicks.Count > PreviewSize) RecentTicks.RemoveAt(RecentTicks.Count - 1);
            }
        }
        catch (OperationCanceledException) { /* expected on Stop */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recorder stream ended");
            Status = $"Stream error: {ex.Message}";
            await StopAsync();
        }
    }

    private async Task TickElapsedAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Elapsed = (DateTime.UtcNow - _startedAtUtc).ToString(@"hh\:mm\:ss");
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _writer?.DisposeAsync().AsTask().Wait();
    }
}

public sealed record RecorderTickPreview(DateTime TimestampUtc, double Bid, double Ask, long BidSize, long AskSize)
{
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");
}
