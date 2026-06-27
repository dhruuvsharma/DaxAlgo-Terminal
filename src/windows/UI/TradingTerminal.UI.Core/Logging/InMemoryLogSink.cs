using System.Collections.ObjectModel;
using System.ComponentModel;

namespace TradingTerminal.UI.Logging;

/// <summary>
/// The single, app-wide activity log. Backs the universal "Activity Log" dock pane and is fed
/// from two directions: the Serilog pipeline (system messages, tagged <c>Source = "System"</c>)
/// and every live strategy / tab view-model, which appends its own activity tagged with its
/// display name. Replaces the per-strategy in-window log panels that used to duplicate this.
///
/// The collection is bounded (oldest entries drop off) and every append is marshalled to the UI
/// thread, so callers on any thread can log without ceremony.
///
/// UI-thread marshalling is pluggable so this type stays WPF-free and is shared by both UI heads:
/// set <see cref="UiPost"/> once at startup — the WPF shell points it at the WPF Dispatcher, the
/// Avalonia shell at <c>Dispatcher.UIThread.Post</c>. The default runs inline (headless/tests).
/// </summary>
public sealed class InMemoryLogSink : INotifyPropertyChanged
{
    private const int CapacityDefault = 2000;

    /// <summary>
    /// Marshals an append onto the UI thread. Assigned once during app startup by whichever UI head
    /// is hosting (WPF / Avalonia). Defaults to inline execution so headless callers and tests work
    /// without a UI thread.
    /// </summary>
    public static Action<Action> UiPost { get; set; } = static action => action();

    public InMemoryLogSink(int capacity = CapacityDefault)
    {
        Capacity = capacity;
        Entries = new ObservableCollection<LogEntry>();
    }

    public int Capacity { get; }
    public ObservableCollection<LogEntry> Entries { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Append(LogEntry entry) => UiPost(() => DoAppend(entry));

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
