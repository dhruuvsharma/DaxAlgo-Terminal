using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;

namespace TradingTerminal.Recording;

/// <summary>
/// One instrument on the recorder watchlist, plus the live counters for each stream it's capturing.
///
/// <para><b>Counter discipline (see the memory-safety skill).</b> The per-stream counters are plain
/// fields bumped with <see cref="Interlocked"/> straight off the feed thread — a feed at tape cadence
/// must never raise a property-changed per event. The panel's 1 s refresh timer calls
/// <see cref="RaiseCounters"/> on the UI thread, so the render cadence is fixed no matter how hot the
/// feed is.</para>
/// </summary>
public sealed partial class RecorderEntry : ObservableObject
{
    internal long QuotesRaw;
    internal long TradesRaw;
    internal long BarsRaw;
    internal long DepthRaw;

    /// <summary>Live subscriptions: the ingest pumps (which do the persisting) plus the hub
    /// subscriptions that feed the counters. Disposed when recording stops or the row is removed.</summary>
    internal readonly List<IDisposable> Subscriptions = new();

    public RecorderEntry(SignalInstrument instrument, BrokerKind? pinnedBroker)
    {
        Instrument = instrument;
        PinnedBroker = pinnedBroker ?? instrument.Broker;
    }

    public SignalInstrument Instrument { get; }

    /// <summary>The broker this row is pinned to, or null to use whichever broker is connected.</summary>
    public BrokerKind? PinnedBroker { get; }

    public string DisplayName => Instrument.DisplayName;
    public string Category => Instrument.Category;
    public string Symbol => Instrument.Contract.Symbol;

    /// <summary>Resolved once recording starts; <see cref="InstrumentId.None"/> until then.</summary>
    public InstrumentId Id { get; internal set; }

    /// <summary>The broker actually serving this row while recording (may differ from
    /// <see cref="PinnedBroker"/> when the pinned one isn't connected).</summary>
    [ObservableProperty] private BrokerKind? _activeBroker;

    /// <summary>Per-row note — which streams are unavailable on this broker, or why it isn't recording.</summary>
    [ObservableProperty] private string? _status;

    [ObservableProperty] private bool _isLive;

    public long Quotes => Interlocked.Read(ref QuotesRaw);
    public long Trades => Interlocked.Read(ref TradesRaw);
    public long Bars => Interlocked.Read(ref BarsRaw);
    public long Depth => Interlocked.Read(ref DepthRaw);

    /// <summary>True when the serving broker publishes a trade tape (IB / Binance / Ironbeam).
    /// Mirrors <see cref="StrategyBrokerCapability.TapeBrokers"/> — the others throw
    /// <see cref="NotSupportedException"/> rather than stream.</summary>
    public bool SupportsTape => ActiveBroker is { } b && StrategyBrokerCapability.TapeBrokers.Contains(b);

    /// <summary>True when the serving broker publishes L2 depth.</summary>
    public bool SupportsDepth => ActiveBroker is { } b && StrategyBrokerCapability.DepthBrokers.Contains(b);

    /// <summary>L3 / market-by-order is not available from ANY backend in this build: there is no
    /// <c>IBrokerClient</c> method for it, no store stream, and no broker feed. The panel shows the L3
    /// chip permanently dimmed rather than pretending. Wiring it up is a broker-seam project.</summary>
    public static bool SupportsL3 => false;

    partial void OnActiveBrokerChanged(BrokerKind? value)
    {
        OnPropertyChanged(nameof(SupportsTape));
        OnPropertyChanged(nameof(SupportsDepth));
    }

    /// <summary>Publishes the counters to the UI. Called on the UI thread by the panel's refresh
    /// timer — never from a feed callback.</summary>
    internal void RaiseCounters()
    {
        OnPropertyChanged(nameof(Quotes));
        OnPropertyChanged(nameof(Trades));
        OnPropertyChanged(nameof(Bars));
        OnPropertyChanged(nameof(Depth));
    }

    internal void ResetCounters()
    {
        Interlocked.Exchange(ref QuotesRaw, 0);
        Interlocked.Exchange(ref TradesRaw, 0);
        Interlocked.Exchange(ref BarsRaw, 0);
        Interlocked.Exchange(ref DepthRaw, 0);
        RaiseCounters();
    }

    internal void DisposeSubscriptions()
    {
        foreach (var sub in Subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* an ingest pump that already faulted must not block teardown of the rest */ }
        }
        Subscriptions.Clear();
        IsLive = false;
        ActiveBroker = null;
    }

    public RecorderWatchlistItem ToWatchlistItem() => new(Symbol, PinnedBroker?.ToString());
}
