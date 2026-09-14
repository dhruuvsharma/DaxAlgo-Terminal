using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Sandbox;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>
/// What the terminal lends a running unit. Everything a block needs from the host arrives here, so a
/// test can run a unit against fakes and the shell decides what is real.
/// </summary>
/// <param name="Hub">Live market data.</param>
/// <param name="Clock">Market time.</param>
/// <param name="AppendActivityLog">The activity log: source, level (INFO/WARN/ERROR/CRITICAL), message.</param>
/// <param name="ShowBanner">Shows an alert to the user.</param>
/// <param name="Store">Recorded history, or null when the host has none.</param>
/// <param name="FindInstrument">Contract details by id, or null when the host has no catalog.</param>
/// <param name="SearchInstruments">Catalog search, or null.</param>
/// <param name="StateStore">Where a unit's state survives restarts, or null to keep it in memory.</param>
/// <param name="OfferExport">Offers text to the user to save; null when the host cannot.</param>
public sealed record BlocksHost(
    IMarketDataHub Hub,
    IClock Clock,
    Action<string, string, string> AppendActivityLog,
    Action<AlertRecord> ShowBanner,
    IMarketDataStore? Store = null,
    Func<InstrumentId, Instrument?>? FindInstrument = null,
    Func<string, int, IReadOnlyList<Instrument>>? SearchInstruments = null,
    IUnitStateStore? StateStore = null,
    Func<string, string, bool>? OfferExport = null);

/// <summary>Limits a runtime enforces.</summary>
/// <param name="MarketQueueCapacity">Market events waiting for the unit thread before the oldest are dropped.</param>
/// <param name="RecentWindow">Items kept per stream for the Recent* reads.</param>
/// <param name="UiFlushInterval">How often coalesced page messages are delivered.</param>
/// <param name="StateSaveInterval">How often dirty state is saved while the unit runs.</param>
/// <param name="ControlQueueCapacity">Posted work (timers, page messages, network results) waiting for the
/// unit thread before further posts are refused and reported.</param>
public sealed record BlocksRuntimeOptions(
    int MarketQueueCapacity = 4096,
    int RecentWindow = 512,
    TimeSpan? UiFlushInterval = null,
    TimeSpan? StateSaveInterval = null,
    int ControlQueueCapacity = 16_384)
{
    public TimeSpan UiFlush => UiFlushInterval ?? TimeSpan.FromMilliseconds(33);
    public TimeSpan StateSave => StateSaveInterval ?? TimeSpan.FromSeconds(60);
}

/// <summary>
/// The page a unit talks to. Implemented by the window that hosts the unit's web UI; the runtime only
/// posts JSON to it and listens for what it sends back.
/// </summary>
public interface IUnitUiEndpoint
{
    /// <summary>True once the page is loaded and has called <c>dax.ready()</c>.</summary>
    bool IsOpen { get; }

    /// <summary>Delivers one message to the page. Called from a runtime timer thread.</summary>
    void Post(string topic, string json);

    /// <summary>A message from the page: topic and JSON payload.</summary>
    event Action<string, string>? MessageReceived;

    /// <summary>The page became ready.</summary>
    event Action? Opened;
}

/// <summary>Where a unit's state survives restarts.</summary>
public interface IUnitStateStore
{
    /// <summary>The saved values for a unit, key to JSON, or null when nothing was saved.</summary>
    IReadOnlyDictionary<string, string>? Load(string unitId);

    /// <summary>Replaces the saved values for a unit.</summary>
    void Save(string unitId, IReadOnlyDictionary<string, string> values);
}
