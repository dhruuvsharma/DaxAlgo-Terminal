using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.UI;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Activity tab — lists every archive in the manifest with restore-from-Telegram action per row.
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
        _ = RefreshAsync();
    }

    public ObservableCollection<ArchiveRow> Rows { get; }

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ArchiveRow? _selectedRow;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await _archiver.ListArchivesAsync();
            Rows.Clear();
            foreach (var e in entries) Rows.Add(ArchiveRow.From(e));
            StatusMessage = entries.Count == 0
                ? "No archives yet — configure Telegram in Archive Settings, then run an Offload."
                : $"{entries.Count} archive(s) on record.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
            _logger.LogError(ex, "Archive activity refresh failed");
        }
        finally { IsBusy = false; }
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
