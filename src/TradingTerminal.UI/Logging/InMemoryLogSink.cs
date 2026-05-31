using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.UI.Logging;

/// <summary>
/// The single, app-wide activity log. Backs the universal "Activity Log" dock pane and is fed
/// from two directions: the Serilog pipeline (system messages, tagged <c>Source = "System"</c>)
/// and every live strategy / tab view-model, which appends its own activity tagged with its
/// display name. Replaces the per-strategy in-window log panels that used to duplicate this.
///
/// The collection is bounded (oldest entries drop off) and every append is marshalled to the UI
/// thread, so callers on any thread can log without ceremony.
/// </summary>
public sealed class InMemoryLogSink : INotifyPropertyChanged
{
    private const int CapacityDefault = 2000;

    public InMemoryLogSink(int capacity = CapacityDefault)
    {
        Capacity = capacity;
        Entries = new ObservableCollection<LogEntry>();
    }

    public int Capacity { get; }
    public ObservableCollection<LogEntry> Entries { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Append(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.CheckAccess())
            DoAppend(entry);
        else
            dispatcher.BeginInvoke(() => DoAppend(entry));
    }

    /// <summary>Convenience append used by strategy/tab view-models — stamps the entry with the
    /// caller's <paramref name="source"/> (its display name) so the universal pane can group and
    /// filter by origin.</summary>
    public void Append(string source, string level, string message) =>
        Append(new LogEntry(DateTime.UtcNow, source, level, message));

    private void DoAppend(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
    }
}

/// <param name="Source">Origin of the entry — "System" for Serilog messages, or a strategy/tab
/// display name. Drives grouping + filtering in the universal Activity Log pane.</param>
public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);
