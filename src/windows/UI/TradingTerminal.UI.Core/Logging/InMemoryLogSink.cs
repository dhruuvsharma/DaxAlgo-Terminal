using System.Collections.ObjectModel;
using System.ComponentModel;

namespace TradingTerminal.UI.Logging;

/// <summary>
/// The single, app-wide activity log. Backs the universal "Activity Log" dock pane and is fed
/// from two directions: the Serilog pipeline (system messages, tagged <c>Source = "System"</c>)
/// and every live strategy / tab view-model, which appends its own activity tagged with its
/// display name. Replaces the per-strategy in-window log panels that used to duplicate this.
///
/// Appends are coalesced: producers enqueue onto a pending buffer from any thread and a single
/// UI-thread flush drains everything accumulated since the last one — one dispatcher hop, one
/// trim pass, and one <see cref="PropertyChanged"/> per batch instead of per entry, so a chatty
/// feed can't saturate the dispatcher or force a main-window layout per log line. The visible
/// collection is bounded (oldest entries drop off) and the pending buffer is bounded the same
/// way, so a burst can never balloon memory before the flush lands.
///
/// UI-thread marshalling is pluggable so this type stays WPF-free and is shared by both UI heads:
/// set <see cref="UiPost"/> once at startup — the WPF shell points it at the WPF Dispatcher, the
/// Avalonia shell at <c>Dispatcher.UIThread.Post</c>. The default runs inline, which keeps
/// appends synchronous for headless callers and tests.
/// </summary>
public sealed class InMemoryLogSink : INotifyPropertyChanged
{
    private const int CapacityDefault = 2000;

    /// <summary>
    /// Marshals a flush onto the UI thread. Assigned once during app startup by whichever UI head
    /// is hosting (WPF / Avalonia). Defaults to inline execution so headless callers and tests work
    /// without a UI thread.
    /// </summary>
    public static Action<Action> UiPost { get; set; } = static action => action();

    private readonly object _gate = new();
    private List<LogEntry> _pending = new();
    private bool _flushScheduled;

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
        bool schedule;
        lock (_gate)
        {
            _pending.Add(entry);
            // Drop-oldest in the burst buffer too, mirroring the visible collection's bound.
            if (_pending.Count > Capacity)
                _pending.RemoveRange(0, _pending.Count - Capacity);
            schedule = !_flushScheduled;
            _flushScheduled = true;
        }
        if (schedule) UiPost(FlushPending);
    }

    /// <summary>Convenience append used by strategy/tab view-models — stamps the entry with the
    /// caller's <paramref name="source"/> (its display name) so the universal pane can group and
    /// filter by origin.</summary>
    public void Append(string source, string level, string message) =>
        Append(new LogEntry(DateTime.UtcNow, source, level, message));

    private void FlushPending()
    {
        List<LogEntry> batch;
        lock (_gate)
        {
            batch = _pending;
            _pending = new List<LogEntry>();
            _flushScheduled = false;
        }
        if (batch.Count == 0) return;

        foreach (var entry in batch) Entries.Add(entry);
        var overflow = Entries.Count - Capacity;
        for (var i = 0; i < overflow; i++) Entries.RemoveAt(0);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
    }
}

/// <param name="Source">Origin of the entry — "System" for Serilog messages, or a strategy/tab
/// display name. Drives grouping + filtering in the universal Activity Log pane.</param>
public sealed record LogEntry(DateTime TimestampUtc, string Source, string Level, string Message);
