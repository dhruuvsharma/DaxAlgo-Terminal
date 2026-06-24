using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.UI;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Archive history tab — the coverage map (which periods of local data are on Telegram vs still
/// pending), a one-click "instant offload" of everything pending, and the list of past uploads with
/// per-row restore.
/// </summary>
public sealed partial class ArchiveActivityViewModel : ViewModelBase
{
    private readonly IMarketDataArchiver _archiver;
    private readonly ILogger<ArchiveActivityViewModel> _logger;

    public ArchiveActivityViewModel(
        IMarketDataArchiver archiver,
        ILogger<ArchiveActivityViewModel> logger)
    {
        _archiver = archiver;
        _logger = logger;
        Rows = new ObservableCollection<ArchiveRow>();
        Coverage = new ObservableCollection<CoverageRow>();
        _ = RefreshAsync();
    }

    public ObservableCollection<ArchiveRow> Rows { get; }

    /// <summary>Period-by-period coverage — each window labelled Offloaded or Pending.</summary>
    public ObservableCollection<CoverageRow> Coverage { get; }

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ArchiveRow? _selectedRow;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private string _coverageSummary = "Scanning local data…";

    /// <summary>True when there's pending data to offload — gates the Instant offload button.</summary>
    public bool HasPending => PendingCount > 0;
    partial void OnPendingCountChanged(int value) => OnPropertyChanged(nameof(HasPending));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await _archiver.ListArchivesAsync();
            Rows.Clear();
            foreach (var e in entries) Rows.Add(ArchiveRow.From(e));

            var coverage = await _archiver.GetCoverageAsync();
            Coverage.Clear();
            foreach (var w in coverage) Coverage.Add(new CoverageRow(w));
            PendingCount = coverage.Count(w => !w.Offloaded);
            CoverageSummary = coverage.Count == 0
                ? "No local data to offload yet."
                : $"{coverage.Count} period(s) · {coverage.Count - PendingCount} offloaded · {PendingCount} pending.";

            StatusMessage = entries.Count == 0
                ? "No archives yet — log in to Telegram in Data → Market data archive, then offload."
                : $"{entries.Count} archive(s) on record.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
            _logger.LogError(ex, "Archive activity refresh failed");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Instantly ship every pending period to Telegram (≤2 GB parts, verified, then pruned).</summary>
    [RelayCommand]
    public async Task InstantOffloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Starting instant offload…";
            var progress = new Progress<string>(line => StatusMessage = line);
            var result = await Task.Run(() => _archiver.OffloadPendingAsync(progress, CancellationToken.None));
            StatusMessage = result.Pending == 0
                ? "Nothing pending — all local data is already on Telegram."
                : $"Instant offload done — {result.Archived}/{result.Pending} offloaded" +
                  (result.Failed > 0 ? $", {result.Failed} failed" : "") + ".";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Instant offload failed: {ex.Message}";
            _logger.LogError(ex, "Instant offload failed");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (SelectedRow?.Entry is not { } entry) return;
        IsBusy = true;
        try
        {
            StatusMessage = $"Restoring #{entry.Id} ({entry.PeriodLabel})…";
            var progress = new Progress<string>(line => StatusMessage = line);
            await Task.Run(() => _archiver.RestoreAsync(entry, progress, CancellationToken.None));
            StatusMessage = $"Restore complete: archive #{entry.Id} ({entry.PeriodLabel}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            _logger.LogError(ex, "Archive restore failed for #{Id}", entry.Id);
        }
        finally { IsBusy = false; }
    }
}

/// <summary>Row-shaped projection of an <see cref="ArchiveManifestEntry"/> for binding.</summary>
public sealed class ArchiveRow
{
    public required ArchiveManifestEntry Entry { get; init; }

    public long Id => Entry.Id;
    public string PeriodLabel => Entry.PeriodLabel;
    public string Range => $"{Entry.FromUtc:yyyy-MM-dd} → {Entry.ToUtc:yyyy-MM-dd}";
    public int Parts => Entry.Parts.Count;
    public string TotalBytesPretty => Fmt(Entry.TotalBytes);
    public string Target => Entry.Target.IsSavedMessages ? "Saved Messages" : (Entry.Target.ChatRef ?? "(unknown)");
    public long RowsQuotes => Entry.RowsQuotes;
    public long RowsBars => Entry.RowsBars;
    public string Uploaded => Entry.UploadedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string LocalDeleted => Entry.DeletedLocal ? "yes" : "no";

    public static ArchiveRow From(ArchiveManifestEntry e) => new() { Entry = e };

    private static string Fmt(long bytes) =>
        bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
        : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
}

/// <summary>Row-shaped projection of one coverage window — a period of local data labelled by
/// whether it's already on Telegram.</summary>
public sealed class CoverageRow
{
    private readonly ArchiveCoverageWindow _w;
    public CoverageRow(ArchiveCoverageWindow w) => _w = w;

    public string PeriodLabel => _w.PeriodLabel;
    public string Range => $"{_w.FromUtc:yyyy-MM-dd} → {_w.ToUtc:yyyy-MM-dd}";
    public bool Offloaded => _w.Offloaded;
    public string Status => _w.Offloaded ? "Offloaded" : "Pending";
    public string ArchiveRef => _w.ArchiveId is { } id ? $"#{id}" : "—";
}
