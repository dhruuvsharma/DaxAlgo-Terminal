using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAPI.Net;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure.CTrader;

/// <summary>
/// Real cTrader client. Talks to Spotware's hosted Open API 2.0 endpoint over TLS+protobuf
/// using the official <c>cTrader.OpenAPI.Net</c> package. The flow is:
///
///   connect → app-auth → account-auth → load symbols (once) → subscribe spots / fetch trendbars.
///
/// Spot prices come back as <c>ulong</c> on the wire and are scaled by <c>10^digits</c>
/// per symbol (digits learned during the symbols-list load). Trendbars come back as
/// deltas relative to <c>Low</c> — we reconstruct OHLC then divide by the same scale.
/// </summary>
public sealed class RealCTraderClient : IBrokerClient
{
    private readonly ILogger<RealCTraderClient> _logger;
    private readonly IOptions<CTraderOptions> _options;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly Dictionary<string, SymbolInfo> _symbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Per-symbol top-of-book size cache, updated by depth events and read by the spot
    /// stream when constructing ticks. Spotware's <c>ProtoOASpotEvent</c> only carries prices
    /// (bid/ask), not sizes — without this cache every tick would emit with BidSize=AskSize=0,
    /// which broke volume-based downstream signals (VPIN, Absorption, Footprint) on cTrader.
    /// Concurrent because depth and spot subscriptions push from different OpenClient threads.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long BidSize, long AskSize)> _topOfBook = new();

    private OpenClient? _client;
    private IDisposable? _messageSub;
    private long _accountId;

    public RealCTraderClient(ILogger<RealCTraderClient> logger, IOptions<CTraderOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public BrokerKind Kind => BrokerKind.CTrader;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var opt = _options.Value;
        if (string.IsNullOrWhiteSpace(opt.ClientId) || string.IsNullOrWhiteSpace(opt.ClientSecret) ||
            string.IsNullOrWhiteSpace(opt.AccessToken))
        {
            _logger.LogError("cTrader credentials incomplete — set ClientId, ClientSecret, AccessToken.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        _accountId = opt.CtidTraderAccountId;

        try
        {
            _client = new OpenClient(opt.Host, opt.Port, TimeSpan.FromSeconds(10));
            await _client.Connect().WaitAsync(ct).ConfigureAwait(false);

            // Subscribe to all incoming messages so we can route responses + errors to per-call TCSes.
            _messageSub = _client.Subscribe(new MessageDispatcher(this));

            await SendAndAwaitAsync<ProtoOAApplicationAuthRes>(
                new ProtoOAApplicationAuthReq
                {
                    ClientId = opt.ClientId,
                    ClientSecret = opt.ClientSecret,
                }, ct).ConfigureAwait(false);

            // If the user didn't pin a CtidTraderAccountId, resolve it from the access token.
            // The internal numeric id isn't user-facing, so auto-discovery spares them hunting
            // for it; we pick the account matching the configured demo/live environment.
            if (_accountId == 0)
            {
                var accountsRes = await SendAndAwaitAsync<ProtoOAGetAccountListByAccessTokenRes>(
                    new ProtoOAGetAccountListByAccessTokenReq { AccessToken = opt.AccessToken }, ct)
                    .ConfigureAwait(false);

                if (accountsRes.CtidTraderAccount.Count == 0)
                    throw new InvalidOperationException("cTrader returned no accounts for this access token.");

                var chosen = accountsRes.CtidTraderAccount.FirstOrDefault(a => a.IsLive == opt.IsLive)
                             ?? accountsRes.CtidTraderAccount[0];
                _accountId = (long)chosen.CtidTraderAccountId;
                _logger.LogInformation(
                    "cTrader auto-resolved account {Account} (isLive={IsLive}) from {Count} available",
                    _accountId, chosen.IsLive, accountsRes.CtidTraderAccount.Count);
            }

            await SendAndAwaitAsync<ProtoOAAccountAuthRes>(
                new ProtoOAAccountAuthReq
                {
                    CtidTraderAccountId = _accountId,
                    AccessToken = opt.AccessToken,
                }, ct).ConfigureAwait(false);

            // One-shot symbols list — we need (symbolName -> symbolId, digits) to translate
            // user-facing tickers and to scale prices reported as ulong.
            var symbolsRes = await SendAndAwaitAsync<ProtoOASymbolsListRes>(
                new ProtoOASymbolsListReq
                {
                    CtidTraderAccountId = _accountId,
                    IncludeArchivedSymbols = false,
                }, ct).ConfigureAwait(false);

            lock (_gate)
            {
                _symbols.Clear();
                foreach (var sym in symbolsRes.Symbol)
                {
                    if (!sym.HasSymbolName) continue;
                    // ProtoOALightSymbol doesn't carry Digits; it's a separate ProtoOASymbol fetch per id.
                    // For v1 default to 5 (FX convention); on first subscribe we resolve and update.
                    _symbols[sym.SymbolName] = new SymbolInfo(sym.SymbolId, Digits: 5);
                }
            }
            _logger.LogInformation("cTrader connected — account {Account}, {Count} symbols loaded",
                _accountId, _symbols.Count);

            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "cTrader connect failed");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _messageSub?.Dispose();
            _messageSub = null;
            _client?.Dispose();
            _client = null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "cTrader disconnect error"); }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        // The full symbol universe is already loaded at connect (ProtoOASymbolsListReq) into
        // _symbols, keyed by symbol name. Subscriptions resolve by contract.Symbol against
        // that same map (see ResolveSymbolAsync), so we emit each name straight through.
        // Symbols missing from this list (e.g. XAUUSD on an FX-only demo account) mean the
        // connected cTID account doesn't have permissions for them — there's no way to surface
        // an instrument cTrader didn't return, so the fix is to choose a broker/account that
        // offers them (IC Markets / Pepperstone / FXPro all include metals + indices + crypto).
        List<TradableInstrument> result;
        lock (_gate)
        {
            result = _symbols.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new TradableInstrument(
                    DisplayName: name,
                    Category: ClassifyCTraderSymbol(name),
                    Contract: new Contract(name, "CASH", "cTrader", string.Empty, PrimaryExchange: string.Empty),
                    Broker: BrokerKind.CTrader))
                .ToList();
        }
        _logger.LogInformation(
            "cTrader instrument list: {Total} symbols ({Fx} FX, {Metals} metals, {Indices} indices, {Energy} energy, {Crypto} crypto, {Stocks} stocks/CFDs)",
            result.Count,
            result.Count(i => i.Category == "FX"),
            result.Count(i => i.Category == "Metals"),
            result.Count(i => i.Category == "Indices"),
            result.Count(i => i.Category == "Energy / Commodities"),
            result.Count(i => i.Category == "Crypto"),
            result.Count(i => i.Category == "Stocks / Other CFD"));
        return Task.FromResult<IReadOnlyList<TradableInstrument>>(result);
    }

    /// <summary>
    /// Bucket a cTrader symbol into one of the picker categories. cTrader symbol names follow
    /// broker-side conventions; the rules here cover IC Markets / Pepperstone / Spotware demo
    /// naming. Anything unclassified falls through to "Stocks / Other CFD" so it still shows.
    /// </summary>
    private static string ClassifyCTraderSymbol(string name)
    {
        var u = name.ToUpperInvariant();

        // Metals — XAUUSD, XAGUSD, XPTUSD, XPDUSD, GOLD, SILVER variants.
        if (u.StartsWith("XAU", StringComparison.Ordinal) || u.StartsWith("XAG", StringComparison.Ordinal)
            || u.StartsWith("XPT", StringComparison.Ordinal) || u.StartsWith("XPD", StringComparison.Ordinal)
            || u.Contains("GOLD", StringComparison.Ordinal) || u.Contains("SILVER", StringComparison.Ordinal)
            || u.Contains("PLATINUM", StringComparison.Ordinal) || u.Contains("PALLADIUM", StringComparison.Ordinal))
            return "Metals";

        // Crypto — BTCUSD/ETHUSD or *USDT pairs, plus common standalone tickers.
        if (u.EndsWith("USDT", StringComparison.Ordinal)
            || ((u.StartsWith("BTC", StringComparison.Ordinal) || u.StartsWith("ETH", StringComparison.Ordinal)
                 || u.StartsWith("XRP", StringComparison.Ordinal) || u.StartsWith("LTC", StringComparison.Ordinal)
                 || u.StartsWith("BCH", StringComparison.Ordinal) || u.StartsWith("ADA", StringComparison.Ordinal)
                 || u.StartsWith("SOL", StringComparison.Ordinal) || u.StartsWith("DOGE", StringComparison.Ordinal)
                 || u.StartsWith("DOT", StringComparison.Ordinal) || u.StartsWith("LINK", StringComparison.Ordinal))
                && (u.EndsWith("USD", StringComparison.Ordinal) || u.EndsWith("EUR", StringComparison.Ordinal))))
            return "Crypto";

        // Energy / commodities — WTI / brent crude, natural gas, coffee, cocoa, sugar, etc.
        if (u.Contains("OIL", StringComparison.Ordinal) || u.Contains("WTI", StringComparison.Ordinal)
            || u.Contains("BRENT", StringComparison.Ordinal) || u.Contains("USOIL", StringComparison.Ordinal)
            || u.Contains("UKOIL", StringComparison.Ordinal) || u.Contains("NGAS", StringComparison.Ordinal)
            || u.Contains("XNG", StringComparison.Ordinal) || u.Contains("XBR", StringComparison.Ordinal)
            || u.Contains("XTI", StringComparison.Ordinal) || u.Contains("COFFEE", StringComparison.Ordinal)
            || u.Contains("COCOA", StringComparison.Ordinal) || u.Contains("SUGAR", StringComparison.Ordinal)
            || u.Contains("COTTON", StringComparison.Ordinal) || u.Contains("WHEAT", StringComparison.Ordinal)
            || u.Contains("CORN", StringComparison.Ordinal) || u.Contains("SOYBEAN", StringComparison.Ordinal))
            return "Energy / Commodities";

        // Indices — names like US30, US500, NAS100, GER40, UK100, JPN225, etc.
        if (u.StartsWith("US30", StringComparison.Ordinal) || u.StartsWith("US100", StringComparison.Ordinal)
            || u.StartsWith("US500", StringComparison.Ordinal) || u.StartsWith("US2000", StringComparison.Ordinal)
            || u.StartsWith("NAS100", StringComparison.Ordinal) || u.StartsWith("SPX500", StringComparison.Ordinal)
            || u.StartsWith("GER", StringComparison.Ordinal) || u.StartsWith("DE30", StringComparison.Ordinal)
            || u.StartsWith("DE40", StringComparison.Ordinal) || u.StartsWith("UK100", StringComparison.Ordinal)
            || u.StartsWith("FRA40", StringComparison.Ordinal) || u.StartsWith("JPN225", StringComparison.Ordinal)
            || u.StartsWith("AUS200", StringComparison.Ordinal) || u.StartsWith("CHN50", StringComparison.Ordinal)
            || u.StartsWith("HK50", StringComparison.Ordinal) || u.StartsWith("ESP35", StringComparison.Ordinal)
            || u.Contains(".CASH", StringComparison.Ordinal) || u.Contains("INDEX", StringComparison.Ordinal))
            return "Indices";

        // FX — 6-letter all-alpha pairs (EURUSD, GBPJPY) or 6-letter with "." separator.
        if (IsForexPair(name)) return "FX";
        if (u.Length == 7 && u[3] == '.' && char.IsLetter(u[0]) && char.IsLetter(u[1])
            && char.IsLetter(u[2]) && char.IsLetter(u[4]) && char.IsLetter(u[5]) && char.IsLetter(u[6]))
            return "FX";

        return "Stocks / Other CFD";
    }

    // A 6-letter all-alpha name (EURUSD, GBPJPY) is a spot FX pair.
    private static bool IsForexPair(string name) =>
        name.Length == 6 && name.All(char.IsLetter);

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected.");
        var symbol = await ResolveSymbolAsync(contract.Symbol, ct).ConfigureAwait(false);

        var period = MapPeriod(barSize);
        var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = toMs - (long)duration.TotalMilliseconds;

        var res = await SendAndAwaitAsync<ProtoOAGetTrendbarsRes>(
            new ProtoOAGetTrendbarsReq
            {
                CtidTraderAccountId = _accountId,
                SymbolId = symbol.SymbolId,
                Period = period,
                FromTimestamp = fromMs,
                ToTimestamp = toMs,
            }, ct).ConfigureAwait(false);

        var scale = Math.Pow(10, symbol.Digits);
        var bars = new List<Bar>(res.Trendbar.Count);
        foreach (var tb in res.Trendbar)
        {
            // Trendbars carry Low (long) and Delta{Open,High,Close} (ulong) — relative encoding.
            var low = tb.Low / scale;
            var open = (tb.Low + (long)tb.DeltaOpen) / scale;
            var high = (tb.Low + (long)tb.DeltaHigh) / scale;
            var close = (tb.Low + (long)tb.DeltaClose) / scale;
            // UtcTimestampInMinutes is minutes since unix epoch.
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(tb.UtcTimestampInMinutes * 60_000L).UtcDateTime;
            bars.Add(new Bar(ts, open, high, low, close, tb.Volume));
        }
        return bars;
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected.");
        // cTrader's live-trendbar feed (ProtoOASubscribeLiveTrendbarReq) emits trendbar diffs;
        // for v1 we synthesize bars from the spot stream, which is simpler and avoids two parallel
        // subscriptions per chart. Aggregate ticks into OHLC at the configured cadence.
        var step = barSize.ToTimeSpan();
        var dropMeter = new FeedDropMeter();
        var ch = FeedChannel.CreateDropOldest<Bar>(FeedChannel.Capacity.Bars, singleWriter: true, onItemDropped: _ =>
        {
            if (dropMeter.Record())
                _logger.LogWarning(
                    "cTrader bar stream for {Symbol} shed its oldest queued bars ({Dropped} total) — consumer is not keeping up",
                    contract.Symbol, dropMeter.Dropped);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                double open = 0, high = 0, low = 0, close = 0;
                long vol = 0;
                var bucketEnd = DateTime.UtcNow + step;
                await foreach (var tick in SubscribeTicksAsync(contract, ct).ConfigureAwait(false))
                {
                    var mid = (tick.Bid + tick.Ask) / 2;
                    if (open == 0) { open = high = low = close = mid; }
                    if (mid > high) high = mid;
                    if (mid < low || low == 0) low = mid;
                    close = mid;
                    vol++;

                    if (DateTime.UtcNow >= bucketEnd)
                    {
                        ch.Writer.TryWrite(new Bar(DateTime.UtcNow, open, high, low, close, vol));
                        open = high = low = close = mid;
                        vol = 0;
                        bucketEnd = DateTime.UtcNow + step;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { ch.Writer.TryComplete(); }
        }, ct);

        await foreach (var bar in ch.Reader.ReadAllAsync(ct))
            yield return bar;
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected.");
        var symbol = await ResolveSymbolAsync(contract.Symbol, ct).ConfigureAwait(false);
        var scale = Math.Pow(10, symbol.Digits);

        var dropMeter = new FeedDropMeter();
        var ch = FeedChannel.CreateDropOldest<Tick>(FeedChannel.Capacity.Quotes, singleWriter: true, onItemDropped: _ =>
        {
            if (dropMeter.Record())
                _logger.LogWarning(
                    "cTrader spot stream for {Symbol} shed its oldest queued ticks ({Dropped} total) — consumer is not keeping up",
                    contract.Symbol, dropMeter.Dropped);
        });

        // Subscribe to spots for this symbol. The shared MessageDispatcher will not route
        // ProtoOASpotEvent through the per-call TCS map; instead we filter the OpenClient's
        // observable here directly so each subscription gets only its own symbol's events.
        using var spotSub = _client!.OfType<ProtoOASpotEvent>()
            .Where(e => e.SymbolId == symbol.SymbolId)
            .Subscribe(spot =>
            {
                if (!spot.HasBid && !spot.HasAsk) return;
                var bid = spot.HasBid ? spot.Bid / scale : 0;
                var ask = spot.HasAsk ? spot.Ask / scale : 0;
                if (bid <= 0 || ask <= 0) return;
                // Pull last-known top-of-book sizes from the depth cache (updated by the depth
                // subscription, if one is active for this symbol). When no depth stream is
                // running for this symbol the sizes stay 0 — same as the legacy behaviour, so
                // pure tick-only consumers keep working. Volume-based signals (VPIN,
                // Absorption) require a concurrent depth subscription, which the Apex strategy
                // already takes via IMarketDataIngest.Subscribe.
                var sizes = _topOfBook.TryGetValue(symbol.SymbolId, out var tob) ? tob : (BidSize: 0L, AskSize: 0L);
                ch.Writer.TryWrite(new Tick(DateTime.UtcNow, bid, ask, sizes.BidSize, sizes.AskSize));
            });

        await SendAndAwaitAsync<ProtoOASubscribeSpotsRes>(
            new ProtoOASubscribeSpotsReq
            {
                CtidTraderAccountId = _accountId,
                SymbolId = { symbol.SymbolId },
            }, ct).ConfigureAwait(false);

        try
        {
            await foreach (var tick in ch.Reader.ReadAllAsync(ct))
                yield return tick;
        }
        finally
        {
            try
            {
                if (_client is not null)
                    await _client.SendMessage(new ProtoOAUnsubscribeSpotsReq
                    {
                        CtidTraderAccountId = _accountId,
                        SymbolId = { symbol.SymbolId },
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "cTrader unsubscribe failed for {Sym}", contract.Symbol); }
            ch.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected.");
        var symbol = await ResolveSymbolAsync(contract.Symbol, ct).ConfigureAwait(false);
        var scale = Math.Pow(10, symbol.Digits);

        // Local order-book reconstruction. Spotware emits incremental ProtoOADepthEvent
        // batches with NewQuotes (add/replace by quote id) and DeletedQuotes (remove by id).
        // Each ProtoOADepthQuote carries either a Bid OR an Ask price + Size; we route it
        // to the right side's map keyed by Id. After each event we emit a consistent
        // top-N snapshot. The lock guards the per-symbol books from a hypothetical race
        // with snapshot construction; today the OpenClient observable is single-threaded
        // but we don't want to depend on that.
        var bidBook = new Dictionary<ulong, DepthLevel>();
        var askBook = new Dictionary<ulong, DepthLevel>();
        var bookLock = new object();
        var firstEventLogged = false;
        var lastCrossedWarnUtc = DateTime.MinValue;

        var dropMeter = new FeedDropMeter();
        var ch = FeedChannel.CreateDropOldest<DepthSnapshot>(FeedChannel.Capacity.Depth, singleWriter: true, onItemDropped: _ =>
        {
            if (dropMeter.Record())
                _logger.LogWarning(
                    "cTrader depth stream for {Symbol} shed its oldest queued snapshots ({Dropped} total) — consumer is not keeping up",
                    contract.Symbol, dropMeter.Dropped);
        });

        using var depthSub = _client!.OfType<ProtoOADepthEvent>()
            // Note: ProtoOADepthEvent.SymbolId is uint64 (unlike ProtoOASpotEvent's int64),
            // so we cast for the comparison. SymbolInfo.SymbolId is long.
            .Where(e => (long)e.SymbolId == symbol.SymbolId)
            .Subscribe(evt =>
            {
                if (!firstEventLogged)
                {
                    firstEventLogged = true;
                    _logger.LogInformation(
                        "cTrader depth: first event for {Sym} (newQuotes={New}, deleted={Del})",
                        contract.Symbol, evt.NewQuotes.Count, evt.DeletedQuotes.Count);
                }
                lock (bookLock)
                {
                    foreach (var deletedId in evt.DeletedQuotes)
                    {
                        bidBook.Remove(deletedId);
                        askBook.Remove(deletedId);
                    }
                    foreach (var q in evt.NewQuotes)
                    {
                        if (q.HasBid)
                        {
                            bidBook[q.Id] = new DepthLevel(q.Bid / scale, (long)q.Size);
                            askBook.Remove(q.Id);
                        }
                        else if (q.HasAsk)
                        {
                            askBook[q.Id] = new DepthLevel(q.Ask / scale, (long)q.Size);
                            bidBook.Remove(q.Id);
                        }
                    }

                    var bidsList = bidBook.Values
                        .OrderByDescending(l => l.Price)
                        .Take(levels)
                        .ToList();
                    var asksList = askBook.Values
                        .OrderBy(l => l.Price)
                        .Take(levels)
                        .ToList();

                    // Mirror top-of-book sizes into the shared cache so the spot stream can
                    // emit ticks with non-zero BidSize/AskSize (Spotware spot events carry
                    // prices only). Downstream volume-based signals (VPIN, Absorption,
                    // FootprintCandle) depend on this.
                    var topBid = bidsList.Count > 0 ? bidsList[0].Size : 0L;
                    var topAsk = asksList.Count > 0 ? asksList[0].Size : 0L;
                    _topOfBook[symbol.SymbolId] = (topBid, topAsk);

                    // Book-integrity guard. cTrader depth has no sequence numbers (in-order TCP),
                    // so there is no gap to resync; the failure mode we *can* see is a crossed or
                    // locked book (best bid >= best ask) from an out-of-order or stale delta. Emit
                    // the snapshot regardless — it self-corrects on the next delta — but surface a
                    // throttled warning to the Activity Log so a persistent crossing is visible.
                    if (bidsList.Count > 0 && asksList.Count > 0 && bidsList[0].Price >= asksList[0].Price)
                    {
                        var nowUtc = DateTime.UtcNow;
                        if (nowUtc - lastCrossedWarnUtc > TimeSpan.FromSeconds(5))
                        {
                            lastCrossedWarnUtc = nowUtc;
                            _logger.LogWarning(
                                "cTrader depth book crossed/locked for {Sym}: best bid {Bid} >= best ask {Ask}. " +
                                "Snapshot emitted anyway; usually self-corrects on the next delta.",
                                contract.Symbol, bidsList[0].Price, asksList[0].Price);
                        }
                    }

                    ch.Writer.TryWrite(new DepthSnapshot(DateTime.UtcNow, bidsList, asksList));
                }
            });

        await SendAndAwaitAsync<ProtoOASubscribeDepthQuotesRes>(
            new ProtoOASubscribeDepthQuotesReq
            {
                CtidTraderAccountId = _accountId,
                SymbolId = { symbol.SymbolId },
            }, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "cTrader depth subscribed: {Sym} (symbolId={Id}). If no depth events arrive, the broker is not serving L2 for this symbol on this account.",
            contract.Symbol, symbol.SymbolId);

        try
        {
            await foreach (var snapshot in ch.Reader.ReadAllAsync(ct))
                yield return snapshot;
        }
        finally
        {
            try
            {
                if (_client is not null)
                    await _client.SendMessage(new ProtoOAUnsubscribeDepthQuotesReq
                    {
                        CtidTraderAccountId = _accountId,
                        SymbolId = { symbol.SymbolId },
                    }).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "cTrader depth unsubscribe failed for {Sym}", contract.Symbol); }
            ch.Writer.TryComplete();
        }
    }

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, CancellationToken ct = default) =>
        // cTrader's Open API does not stream individual trade prints — ProtoOASpotEvent carries
        // bid/ask updates and ProtoOADepthEvent carries order-book changes, but there is no
        // first-class "tape" channel. Tape reconstruction from spot ticks is lossy and not
        // attempted here. Ingest swallows the NotSupportedException.
        throw new NotSupportedException(
            "cTrader Open API does not expose a trade-tape channel.");

    private async Task<SymbolInfo> ResolveSymbolAsync(string symbolName, CancellationToken ct)
    {
        SymbolInfo? cached;
        lock (_gate) _symbols.TryGetValue(symbolName, out cached);
        if (cached is null)
            throw new InvalidOperationException($"Symbol '{symbolName}' is not exposed by this cTrader account.");

        if (cached.Digits != 5) return cached; // already enriched

        // Fetch full ProtoOASymbol so we know the actual digits for price scaling.
        try
        {
            var res = await SendAndAwaitAsync<ProtoOASymbolByIdRes>(
                new ProtoOASymbolByIdReq
                {
                    CtidTraderAccountId = _accountId,
                    SymbolId = { cached.SymbolId },
                }, ct).ConfigureAwait(false);
            var detail = res.Symbol.FirstOrDefault();
            if (detail is not null && detail.HasDigits)
            {
                var enriched = cached with { Digits = detail.Digits };
                lock (_gate) _symbols[symbolName] = enriched;
                return enriched;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Symbol detail fetch failed for {Sym}; using default digits=5", symbolName);
        }
        return cached;
    }

    private static ProtoOATrendbarPeriod MapPeriod(BarSize barSize) => barSize switch
    {
        BarSize.OneMinute => ProtoOATrendbarPeriod.M1,
        BarSize.ThreeMinutes => ProtoOATrendbarPeriod.M3,
        BarSize.FiveMinutes => ProtoOATrendbarPeriod.M5,
        BarSize.FifteenMinutes => ProtoOATrendbarPeriod.M15,
        BarSize.OneHour => ProtoOATrendbarPeriod.H1,
        BarSize.OneDay => ProtoOATrendbarPeriod.D1,
        _ => ProtoOATrendbarPeriod.M1,
    };

    // ---- Per-call response correlation ----------------------------------------------------------

    private readonly Dictionary<string, TaskCompletionSource<IMessage>> _pendingByMsgId = new();
    private readonly Dictionary<int, TaskCompletionSource<IMessage>> _pendingByPayloadType = new();

    private async Task<TRes> SendAndAwaitAsync<TRes>(IMessage req, CancellationToken ct)
        where TRes : class, IMessage
    {
        if (_client is null) throw new InvalidOperationException("Not connected.");

        var msgId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pendingByMsgId[msgId] = tcs;

        try
        {
            await _client.SendMessage((dynamic)req, msgId).ConfigureAwait(false);
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var res = await tcs.Task.ConfigureAwait(false);
            if (res is TRes typed) return typed;
            if (res is ProtoOAErrorRes err)
                throw new InvalidOperationException($"cTrader error {err.ErrorCode}: {err.Description}");
            throw new InvalidOperationException($"cTrader: unexpected response {res.GetType().Name}");
        }
        finally
        {
            lock (_gate) _pendingByMsgId.Remove(msgId);
        }
    }

    private void RouteIncomingMessage(IMessage message, string? clientMsgId)
    {
        if (clientMsgId is null) return;
        TaskCompletionSource<IMessage>? tcs;
        lock (_gate) _pendingByMsgId.TryGetValue(clientMsgId, out tcs);
        tcs?.TrySetResult(message);
    }

    public ValueTask DisposeAsync()
    {
        try { _messageSub?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        _state.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record SymbolInfo(long SymbolId, int Digits);

    /// <summary>
    /// Bridges OpenClient's <c>IObservable&lt;IMessage&gt;</c> into the request/response router.
    /// Spot events (and other broadcast events) are still emitted on the OpenClient stream so
    /// per-symbol subscribers can pick them up directly.
    /// </summary>
    private sealed class MessageDispatcher : IObserver<IMessage>
    {
        private readonly RealCTraderClient _owner;
        public MessageDispatcher(RealCTraderClient owner) => _owner = owner;

        public void OnNext(IMessage value)
        {
            // The OpenClient unwraps ProtoMessage envelopes into concrete typed messages, but the
            // ClientMsgId lives on the envelope. ProtoMessage itself is also forwarded on the
            // stream; route through that to find the matching pending TCS.
            if (value is ProtoMessage envelope && envelope.HasClientMsgId)
            {
                // The library will also push the parsed inner message to subscribers; here we just
                // need the envelope for the clientMsgId. We resolve the inner type from PayloadType.
                _owner.RouteByEnvelopeAsync(envelope);
            }
        }

        public void OnError(Exception error)
        {
            _owner._logger.LogWarning(error, "cTrader message stream error");
            _owner._state.OnNext(Core.Domain.ConnectionState.Disconnected);
        }

        public void OnCompleted()
        {
            _owner._logger.LogInformation("cTrader message stream completed");
            _owner._state.OnNext(Core.Domain.ConnectionState.Disconnected);
        }
    }

    private void RouteByEnvelopeAsync(ProtoMessage envelope)
    {
        if (!envelope.HasClientMsgId) return;
        IMessage? inner = TryDecodeInner(envelope);
        if (inner is null) return;
        RouteIncomingMessage(inner, envelope.ClientMsgId);
    }

    private static IMessage? TryDecodeInner(ProtoMessage envelope)
    {
        try
        {
            return envelope.PayloadType switch
            {
                (int)ProtoOAPayloadType.ProtoOaApplicationAuthRes => ProtoOAApplicationAuthRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenRes => ProtoOAGetAccountListByAccessTokenRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaAccountAuthRes => ProtoOAAccountAuthRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaSymbolsListRes => ProtoOASymbolsListRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaSymbolByIdRes => ProtoOASymbolByIdRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaSubscribeSpotsRes => ProtoOASubscribeSpotsRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaSubscribeDepthQuotesRes => ProtoOASubscribeDepthQuotesRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaUnsubscribeDepthQuotesRes => ProtoOAUnsubscribeDepthQuotesRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaGetTrendbarsRes => ProtoOAGetTrendbarsRes.Parser.ParseFrom(envelope.Payload),
                (int)ProtoOAPayloadType.ProtoOaErrorRes => ProtoOAErrorRes.Parser.ParseFrom(envelope.Payload),
                _ => null,
            };
        }
        catch { return null; }
    }
}
