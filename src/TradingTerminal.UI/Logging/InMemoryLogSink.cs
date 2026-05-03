using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.UI.Logging;

/// <summary>An observable, capped collection used by the Logs pane for live tailing.</summary>
public sealed class InMemoryLogSink : INotifyPropertyChanged
{
    private const int CapacityDefault = 500;

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

    private void DoAppend(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
    }
}

public sealed record LogEntry(DateTime TimestampUtc, string Level, string Message);
