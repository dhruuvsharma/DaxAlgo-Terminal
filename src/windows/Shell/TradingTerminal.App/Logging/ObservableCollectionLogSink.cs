using Serilog.Core;
using Serilog.Events;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Logging;

/// <summary>Serilog sink that forwards rendered messages into an <see cref="InMemoryLogSink"/>.</summary>
public sealed class ObservableCollectionLogSink : ILogEventSink
{
    private readonly InMemoryLogSink _ui;

    public ObservableCollectionLogSink(InMemoryLogSink ui) => _ui = ui;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp.UtcDateTime,
            "System",
            logEvent.Level.ToString(),
            logEvent.RenderMessage());
        _ui.Append(entry);
    }
}
