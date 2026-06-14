using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.OrderBook;

/// <summary>
/// Drives the standalone Order Book window. Mirrors <c>ChartsViewModel</c> in shape — it owns the
/// instrument picker universe and resolves the source broker — but instead of bars it subscribes to
/// <see cref="IMarketDataHub.Depth"/> and renders the full L2 ladder for the selected instrument.
///
/// The streaming path is the canonical one: a single <see cref="IMarketDataIngest.Subscribe"/> handle
/// starts (or joins) the ref-counted broker L1/L2 pump, and this VM observes the hub's
/// <see cref="DepthSnapshot"/> stream keyed by <see cref="InstrumentId"/>. Every snapshot is a
/// consistent whole-book picture (the ingest layer reconstructs it from broker depth events), so each
/// update simply rebuilds the <see cref="Asks"/> / <see cref="Bids"/> ladders — no diff bookkeeping.
/// Brokers without L2 (Alpaca, IB-not-yet-wired) produce no depth events; the pane just stays empty
/// and the status line says so.
/// </summary>
public sealed partial class OrderBookViewModel : ViewModelBase, IDisposable
{
    /// <summary>Cap on how many instruments the picker shows at once (the broker universe can be
    /// thousands of symbols; the search box narrows it).</summary>
    public const int MaxInstrumentsDisplayed = 500;

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IBrokerSelector _selector;
    private readonly ILogger<OrderBookViewModel> _logger;

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _ingestHandle;

    public OrderBookViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<OrderBookViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _selector = selector;
        _logger = logger;

        _allInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        _ = LoadInstrumentsAsync();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }

    /// <summary>Ask side, displayed best-ask-at-the-bottom (highest price first) so the spread sits
    /// between the two ladders like a classic depth-of-market.</summary>
    [ObservableProperty] private ObservableCollection<OrderBookLevel> _asks = new();

    /// <summary>Bid side, best-bid first (descending price).</summary>
    [ObservableProperty] private ObservableCollection<OrderBookLevel> _bids = new();

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _status = "Pick an instrument to stream its order book.";
    [ObservableProperty] private double? _bestBid;
    [ObservableProperty] private double? _bestAsk;
    [ObservableProperty] private double? _spread;
    [ObservableProperty] private double? _mid;
    [ObservableProperty] private int _bidLevels;
    [ObservableProperty] private int _askLevels;
    [ObservableProperty] private DateTime? _lastUpdateUtc;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) => Restart();

    /// <summary>Swaps the static fallback catalog for the connected broker's tradable universe,
    /// mapped to <see cref="SignalInstrument"/>. Keeps the static catalog on failure / empty list.</summary>
    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            _allInstruments = list
                .Select(i => new SignalInstrument($"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}", i.Category, i.Contract, i.Broker))
                .ToList();

            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: instrument list load failed; using static catalog");
        }
    }

    private void ApplyFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = _allInstruments;
        if (term.Length > 0)
            query = _allInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = query.Take(MaxInstrumentsDisplayed).ToList();
        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments.Clear();
        foreach (var inst in shown) Instruments.Add(inst);
    }

    private void Restart()
    {
        StopStream();
        var instrument = SelectedInstrument;
        if (instrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; ClearBook(); return; }

        try
        {
            var id = _ingest.Resolve(instrument.Contract, broker);
            _streamCts = new CancellationTokenSource();
            // Single ref-counted handle powers both L1 and depth on the ingest side.
            _ingestHandle = _ingest.Subscribe(instrument.Contract, broker);
            Status = $"Streaming order book — {instrument.DisplayName} ({BrokerLabel(broker)})…";
            _ = RunDepthStreamAsync(id, _streamCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: subscribe failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Subscribe failed: {ex.Message}";
        }
    }

    /// <summary>Pumps depth snapshots off the hub through a bounded channel (so the publish thread is
    /// never blocked by the UI) and rebuilds the ladders on the UI thread for the freshest one.</summary>
    private const int DepthChannelCapacity = 2_048;

    private async Task RunDepthStreamAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        // Bounded + DropOldest so a fast book the UI can't keep up with is capped in memory (newest
        // snapshot wins) instead of piling an unbounded backlog into GBs. We render only the freshest
        // snapshot per drain — intermediate books are already stale, so coalescing is lossless here.
        var channel = Channel.CreateBounded<DepthSnapshot>(new BoundedChannelOptions(DepthChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _hub.Depth(instrumentId).Subscribe(s => channel.Writer.TryWrite(s));

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                DepthSnapshot? latest = null;
                while (channel.Reader.TryRead(out var s)) latest = s;
                if (latest is { } snap) await UiThread.RunAsync(() => Render(snap));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Order book: depth stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Projects a whole-book snapshot into the two display ladders, computing cumulative
    /// sizes and a 0..1 bar fraction (relative to the largest level on either side) for the depth bars.</summary>
    private void Render(DepthSnapshot snapshot)
    {
        long maxSize = 1;
        foreach (var l in snapshot.Bids) if (l.Size > maxSize) maxSize = l.Size;
        foreach (var l in snapshot.Asks) if (l.Size > maxSize) maxSize = l.Size;

        // Bids: best-first (already descending). Cumulative from the top of book down.
        var bids = new ObservableCollection<OrderBookLevel>();
        long cum = 0;
        foreach (var l in snapshot.Bids)
        {
            cum += l.Size;
            bids.Add(new OrderBookLevel(l.Price, l.Size, cum, (double)l.Size / maxSize));
        }

        // Asks: cumulative is computed best-first (ascending), but displayed highest-price-first so
        // the best ask sits just above the spread. Reverse after accumulating.
        var asksBestFirst = new List<OrderBookLevel>();
        cum = 0;
        foreach (var l in snapshot.Asks)
        {
            cum += l.Size;
            asksBestFirst.Add(new OrderBookLevel(l.Price, l.Size, cum, (double)l.Size / maxSize));
        }
        asksBestFirst.Reverse();
        var asks = new ObservableCollection<OrderBookLevel>(asksBestFirst);

        Bids = bids;
        Asks = asks;
        BidLevels = snapshot.Bids.Count;
        AskLevels = snapshot.Asks.Count;
        BestBid = snapshot.BestBid > 0 ? snapshot.BestBid : null;
        BestAsk = snapshot.BestAsk > 0 ? snapshot.BestAsk : null;
        Spread = BestBid is { } b && BestAsk is { } a ? a - b : null;
        Mid = BestBid is { } bb && BestAsk is { } aa ? (aa + bb) * 0.5 : null;
        LastUpdateUtc = snapshot.TimestampUtc;
        Status = $"{SelectedInstrument?.DisplayName} · {snapshot.Bids.Count} bid / {snapshot.Asks.Count} ask levels · {snapshot.TimestampUtc:HH:mm:ss}";
    }

    private void ClearBook()
    {
        Bids = new ObservableCollection<OrderBookLevel>();
        Asks = new ObservableCollection<OrderBookLevel>();
        BidLevels = AskLevels = 0;
        BestBid = BestAsk = Spread = Mid = null;
        LastUpdateUtc = null;
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    private static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _ingestHandle?.Dispose();
        _ingestHandle = null;
    }

    public void Dispose() => StopStream();
}

/// <summary>One display row of the ladder. <see cref="BarFraction"/> is the level size relative to the
/// largest level on either side (0..1), used to draw the proportional depth bar in the view.</summary>
public sealed record OrderBookLevel(double Price, long Size, long Cumulative, double BarFraction);
