using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Controls.Render;

namespace TradingTerminal.UI.Strategies;

/// <summary>
/// Starts the market-data feeds an authored unit needs, and holds them for as long as its window is
/// open.
///
/// <para><b>The missing half of every authored unit, and the reason a generated visualizer drew
/// nothing.</b> The sandbox runtime subscribes to <c>IMarketDataHub</c> — quotes, trades, bars, depth
/// — and the hub is a bus, not a source: it carries only what some broker has been ASKED to stream.
/// Asking is <see cref="IMarketDataIngest"/>, and nothing in the authored-unit path ever called it. So
/// a unit opened, its runtime subscribed correctly to four streams, and every one of them was silent
/// unless another window happened to be streaming the same instrument at the time — which is exactly
/// how the bug presented: it worked when someone had the order book open beside it, and not
/// otherwise.</para>
///
/// <para>Every tool window in the app does this already — <c>_ingest.Subscribe(contract, broker)</c>
/// on restart, disposed on stop. This is that, keyed off the requirement the unit itself declared.</para>
///
/// <para><b>Never throws.</b> A venue that refuses one stream must not stop the window opening: the
/// failure is logged and the other streams stand, because a book with no tape is still a book.</para>
/// </summary>
public sealed class AuthoredUnitFeed : IDisposable
{
    private readonly List<IDisposable> _handles;
    private int _disposed;

    private AuthoredUnitFeed(List<IDisposable> handles, string streams)
    {
        _handles = handles;
        Streams = streams;
    }

    /// <summary>Nothing was started — no instrument, or no requirement that needs a feed.</summary>
    public static AuthoredUnitFeed None { get; } = new([], string.Empty);

    /// <summary>What was started, for the activity log: "depth + L1, tape". Empty when nothing was.</summary>
    public string Streams { get; }

    /// <summary>True when at least one feed is running.</summary>
    public bool IsLive => _handles.Count > 0;

    /// <summary>
    /// Opens the feeds <paramref name="requirement"/> calls for on <paramref name="instrument"/>.
    /// </summary>
    /// <param name="ingest">The ingest seam. Its handles are ref-counted, so joining a stream another
    /// window already started costs nothing and releasing ours does not stop theirs.</param>
    /// <param name="instrument">The picked row, carrying the contract and the broker its id was
    /// resolved against. Null — an unresolved parameter, a preview — starts nothing.</param>
    /// <param name="requirement">What the unit declared it consumes.</param>
    /// <param name="barSize">Which bar feed to start when the unit wants bars. One size rather than
    /// all six: a bar subscription is a real request to a venue, and a unit that asks for bars almost
    /// always means minute bars. The runtime listens on every size regardless, so a venue that also
    /// publishes others still reaches the unit.</param>
    /// <param name="logger">Optional; a refused stream is logged here.</param>
    public static AuthoredUnitFeed Open(
        IMarketDataIngest ingest,
        AuthoredUnitInstrument? instrument,
        StrategyDataRequirement requirement,
        BarSize barSize = BarSize.OneMinute,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        if (instrument is null) return None;

        var contract = instrument.Instrument.Contract;
        var broker = instrument.Broker;
        var handles = new List<IDisposable>();
        var started = new List<string>();

        // One handle powers both quotes and depth on the ingest side, which is why this is a single
        // call for two requirements rather than two calls that would double the reference count.
        if ((requirement & (StrategyDataRequirement.L1 | StrategyDataRequirement.Depth)) != 0)
        {
            Start(() => ingest.Subscribe(contract, broker), handles, started,
                (requirement & StrategyDataRequirement.Depth) != 0 ? "depth + L1" : "L1",
                contract, broker, logger);
        }

        if ((requirement & StrategyDataRequirement.TradeTape) != 0)
        {
            Start(() => ingest.SubscribeTrades(contract, broker), handles, started, "tape",
                contract, broker, logger);
        }

        if ((requirement & StrategyDataRequirement.Bars) != 0)
        {
            Start(() => ingest.SubscribeBars(contract, broker, barSize), handles, started, $"{barSize} bars",
                contract, broker, logger);
        }

        return handles.Count == 0 ? None : new AuthoredUnitFeed(handles, string.Join(", ", started));
    }

    /// <summary>
    /// The same, for a unit that declared more than one instrument parameter — a spread, two venues
    /// side by side. One bag of handles so the window has one thing to dispose.
    /// </summary>
    public static AuthoredUnitFeed OpenAll(
        IMarketDataIngest ingest,
        IEnumerable<AuthoredUnitInstrument?> instruments,
        StrategyDataRequirement requirement,
        BarSize barSize = BarSize.OneMinute,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(instruments);

        var handles = new List<IDisposable>();
        var started = new List<string>();

        foreach (var instrument in instruments)
        {
            var feed = Open(ingest, instrument, requirement, barSize, logger);
            if (!feed.IsLive) continue;

            handles.AddRange(feed._handles);
            started.Add($"{instrument!.DisplayName} ({feed.Streams})");

            // The handles moved into this bag; stop the source disposing them under us.
            feed._handles.Clear();
        }

        return handles.Count == 0 ? None : new AuthoredUnitFeed(handles, string.Join("; ", started));
    }

    private static void Start(
        Func<IDisposable> subscribe,
        List<IDisposable> handles,
        List<string> started,
        string label,
        Contract contract,
        Core.Brokers.BrokerKind broker,
        ILogger? logger)
    {
        try
        {
            handles.Add(subscribe());
            started.Add(label);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Authored unit: {Stream} feed refused for {Symbol} on {Broker}", label, contract.Symbol, broker);
        }
    }

    /// <summary>Releases this window's reference to each stream. Another window holding the same one
    /// keeps it running.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var handle in _handles)
        {
            try { handle.Dispose(); }
            catch (Exception) { /* a broker already gone is not a reason to leak the rest */ }
        }

        _handles.Clear();
    }
}
