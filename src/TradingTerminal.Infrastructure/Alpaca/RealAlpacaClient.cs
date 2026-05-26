using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Alpaca.Markets;
using Alpaca.Markets.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Alpaca;

/// <summary>
/// Real Alpaca client. Talks to the hosted Alpaca Market Data + Trading APIs over REST
/// (history) and WebSocket (live quotes). One client instance multiplexes three asset
/// classes routed by <see cref="Contract.SecType"/>:
///
///   "STK"    → stock data client + stock streaming client
///   "CRYPTO" → crypto data client + crypto streaming client
///   "OPT"    → not yet supported by this wrapper (Alpaca's options SDK is stabilising)
///
/// Live vs paper is selected at connect time from <see cref="AlpacaOptions.IsLive"/>; the
/// SDK's <c>Environments.Live</c> / <c>Environments.Paper</c> factories pick the right
/// endpoint set. Auth is a single API key + secret — no OAuth.
///
/// Alpaca does not expose L2 depth-of-market, so <see cref="SubscribeDepthAsync"/> throws
/// <see cref="NotSupportedException"/>; strategies that need depth must route through
/// IB or cTrader (see <see cref="IBrokerClient.SubscribeDepthAsync"/> contract).
/// </summary>
public sealed class RealAlpacaClient : IBrokerClient
{
    private readonly ILogger<RealAlpacaClient> _logger;
    private readonly IOptions<AlpacaOptions> _options;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    private IAlpacaTradingClient? _trading;
    private IAlpacaDataClient? _stockData;
    private IAlpacaDataStreamingClient? _stockStream;
    private IAlpacaCryptoDataClient? _cryptoData;
    private IAlpacaCryptoStreamingClient? _cryptoStream;

    public RealAlpacaClient(ILogger<RealAlpacaClient> logger, IOptions<AlpacaOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public BrokerKind Kind => BrokerKind.Alpaca;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public IObservable<OrderEvent> OrderEvents { get; } = Observable.Empty<OrderEvent>();

    public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("OMS not yet wired for Alpaca.");

    public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) =>
        throw new NotSupportedException("OMS not yet wired for Alpaca.");

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var opt = _options.Value;
        if (string.IsNullOrWhiteSpace(opt.ApiKey) || string.IsNullOrWhiteSpace(opt.ApiSecret))
        {
            _logger.LogError("Alpaca credentials incomplete — set ApiKey and ApiSecret.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        try
        {
            var env = opt.IsLive ? Environments.Live : Environments.Paper;
            var creds = new SecretKey(opt.ApiKey, opt.ApiSecret);

            _trading = env.GetAlpacaTradingClient(creds);
            _stockData = env.GetAlpacaDataClient(creds);

            // Pin the live stock stream to the configured feed (IEX on the free/paper plan).
            // The SDK's default data-stream endpoint targets SIP, which a free subscription
            // can't subscribe to — the same entitlement wall as the historical SIP fetch.
            // We rebase only the feed path segment so the host stays whatever the SDK uses.
            var stockStreamCfg = env.GetAlpacaDataStreamingClientConfiguration(creds);
            stockStreamCfg.ApiEndpoint = new Uri(stockStreamCfg.ApiEndpoint, $"/v2/{FeedSegment(opt.StockDataFeed)}");
            _stockStream = stockStreamCfg.GetClient();

            _cryptoData = env.GetAlpacaCryptoDataClient(creds);
            _cryptoStream = env.GetAlpacaCryptoStreamingClient(creds);

            // Both streaming clients need an explicit connect + auth before SubscribeAsync.
            // We do it eagerly here so the first subscription doesn't pay the auth round-trip.
            var stockAuth = await _stockStream.ConnectAndAuthenticateAsync(ct).ConfigureAwait(false);
            if (stockAuth != AuthStatus.Authorized)
                throw new InvalidOperationException($"Alpaca stock stream auth failed: {stockAuth}");

            var cryptoAuth = await _cryptoStream.ConnectAndAuthenticateAsync(ct).ConfigureAwait(false);
            if (cryptoAuth != AuthStatus.Authorized)
                throw new InvalidOperationException($"Alpaca crypto stream auth failed: {cryptoAuth}");

            _logger.LogInformation("Alpaca connected ({Mode}, stock-feed={Feed})",
                opt.IsLive ? "live" : "paper", opt.StockDataFeed);

            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alpaca connect failed");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var result = new List<TradableInstrument>();

        // US equities — active and tradable only. Alpaca returns the full universe
        // (~11k symbols) in a single call; the UI filters/searches over it.
        var equities = await _trading!.ListAssetsAsync(
            new AssetsRequest
            {
                AssetStatus = AssetStatus.Active,
                AssetClass = global::Alpaca.Markets.AssetClass.UsEquity,
            }, ct).ConfigureAwait(false);
        foreach (var a in equities)
        {
            if (!a.IsTradable) continue;
            var name = string.IsNullOrWhiteSpace(a.Name) ? a.Symbol : a.Name;
            result.Add(new TradableInstrument(
                $"{a.Symbol}  —  {name}", "US Stocks", Contract.UsStock(a.Symbol), BrokerKind.Alpaca));
        }

        // Crypto — the symbol Alpaca returns (e.g. "BTC/USD") is exactly what the
        // streaming/data clients expect, so it flows straight into the Contract.
        var crypto = await _trading.ListAssetsAsync(
            new AssetsRequest
            {
                AssetStatus = AssetStatus.Active,
                AssetClass = global::Alpaca.Markets.AssetClass.Crypto,
            }, ct).ConfigureAwait(false);
        foreach (var a in crypto)
        {
            if (!a.IsTradable) continue;
            var name = string.IsNullOrWhiteSpace(a.Name) ? a.Symbol : a.Name;
            result.Add(new TradableInstrument(
                $"{a.Symbol}  —  {name}", "Crypto",
                new Contract(a.Symbol, "CRYPTO", "SMART", "USD", PrimaryExchange: string.Empty),
                BrokerKind.Alpaca));
        }

        _logger.LogInformation("Alpaca listed {Stocks} equities + {Crypto} crypto instruments",
            equities.Count(x => x.IsTradable), crypto.Count(x => x.IsTradable));
        return result;
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_stockStream is not null) await _stockStream.DisconnectAsync(ct).ConfigureAwait(false);
            if (_cryptoStream is not null) await _cryptoStream.DisconnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Alpaca disconnect error"); }

        _stockStream?.Dispose(); _stockStream = null;
        _cryptoStream?.Dispose(); _cryptoStream = null;
        _stockData?.Dispose(); _stockData = null;
        _cryptoData?.Dispose(); _cryptoData = null;
        _trading?.Dispose(); _trading = null;

        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        EnsureConnected();
        var to = DateTime.UtcNow;
        var from = to - duration;
        var timeFrame = MapBarSize(barSize);
        var assetClass = ClassifyAsset(contract);

        if (assetClass == AssetClass.Stock)
        {
            // Without an explicit feed Alpaca defaults to SIP, which the free/paper plan
            // can't query for recent bars ("subscription does not permit querying recent
            // SIP data"). Pin it to the configured feed (IEX by default).
            var req = new HistoricalBarsRequest(contract.Symbol, from, to, timeFrame)
            {
                Feed = MapFeed(_options.Value.StockDataFeed),
            };
            var resp = await _stockData!.ListHistoricalBarsAsync(req, ct).ConfigureAwait(false);
            return resp.Items.Select(MapBar).ToList();
        }
        if (assetClass == AssetClass.Crypto)
        {
            var req = new HistoricalCryptoBarsRequest(new[] { contract.Symbol }, from, to, timeFrame);
            var resp = await _cryptoData!.ListHistoricalBarsAsync(req, ct).ConfigureAwait(false);
            return resp.Items.Select(MapBar).ToList();
        }
        throw new NotSupportedException(
            $"Alpaca: historical bars for SecType '{contract.SecType}' are not yet supported. " +
            "Use STK or CRYPTO.");
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Alpaca exposes a live-minute-bar subscription, but to keep the bar cadence
        // configurable (1-minute / 5-minute / etc.) we aggregate from the tick stream
        // — same approach as cTrader. Strategies that want native minute bars can
        // route through RequestHistoricalBarsAsync + a single live-bar subscription.
        var step = barSize.ToTimeSpan();
        var ch = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
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
        EnsureConnected();
        var assetClass = ClassifyAsset(contract);

        var ch = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Per-asset-class subscription factory + Received handler. The SDK exposes
        // a per-symbol subscription object you wire an event to, then activate via
        // SubscribeAsync. We hold the handler so we can unhook on disposal.
        if (assetClass == AssetClass.Stock)
        {
            var sub = _stockStream!.GetQuoteSubscription(contract.Symbol);
            void OnQuote(IQuote q)
            {
                if (q.BidPrice <= 0 || q.AskPrice <= 0) return;
                ch.Writer.TryWrite(new Tick(
                    q.TimestampUtc,
                    (double)q.BidPrice,
                    (double)q.AskPrice,
                    (long)q.BidSize,
                    (long)q.AskSize));
            }
            sub.Received += OnQuote;
            await _stockStream.SubscribeAsync(sub, ct).ConfigureAwait(false);

            try
            {
                await foreach (var tick in ch.Reader.ReadAllAsync(ct))
                    yield return tick;
            }
            finally
            {
                sub.Received -= OnQuote;
                try { await _stockStream.UnsubscribeAsync(sub, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Alpaca stock unsubscribe failed for {Sym}", contract.Symbol); }
                ch.Writer.TryComplete();
            }
        }
        else if (assetClass == AssetClass.Crypto)
        {
            var sub = _cryptoStream!.GetQuoteSubscription(contract.Symbol);
            void OnQuote(IQuote q)
            {
                if (q.BidPrice <= 0 || q.AskPrice <= 0) return;
                ch.Writer.TryWrite(new Tick(
                    q.TimestampUtc,
                    (double)q.BidPrice,
                    (double)q.AskPrice,
                    (long)q.BidSize,
                    (long)q.AskSize));
            }
            sub.Received += OnQuote;
            await _cryptoStream.SubscribeAsync(sub, ct).ConfigureAwait(false);

            try
            {
                await foreach (var tick in ch.Reader.ReadAllAsync(ct))
                    yield return tick;
            }
            finally
            {
                sub.Received -= OnQuote;
                try { await _cryptoStream.UnsubscribeAsync(sub, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogDebug(ex, "Alpaca crypto unsubscribe failed for {Sym}", contract.Symbol); }
                ch.Writer.TryComplete();
            }
        }
        else
        {
            throw new NotSupportedException(
                $"Alpaca: live ticks for SecType '{contract.SecType}' are not yet supported. " +
                "Use STK or CRYPTO.");
        }
    }

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Alpaca does not expose L2 depth. Route depth subscriptions through IB or cTrader.");

    private void EnsureConnected()
    {
        if (_trading is null || _stockData is null || _stockStream is null || _cryptoData is null || _cryptoStream is null)
            throw new InvalidOperationException("Not connected — call ConnectAsync first.");
    }

    private static Bar MapBar(IBar b) =>
        new(b.TimeUtc, (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close, (long)b.Volume);

    private static AssetClass ClassifyAsset(Contract c) => c.SecType?.ToUpperInvariant() switch
    {
        "STK" => AssetClass.Stock,
        "STOCK" => AssetClass.Stock,
        "EQUITY" => AssetClass.Stock,
        "CRYPTO" => AssetClass.Crypto,
        "CRYPTOCURRENCY" => AssetClass.Crypto,
        _ => AssetClass.Other,
    };

    // Free/paper subscriptions are entitled only to IEX; "sip"/"otc" require a paid data plan.
    private static MarketDataFeed MapFeed(string? feed) => feed?.Trim().ToLowerInvariant() switch
    {
        "sip" => MarketDataFeed.Sip,
        "otc" => MarketDataFeed.Otc,
        _ => MarketDataFeed.Iex,
    };

    // The data-stream WebSocket selects the feed by its final path segment (/v2/iex|sip|otc).
    private static string FeedSegment(string? feed) => MapFeed(feed) switch
    {
        MarketDataFeed.Sip => "sip",
        MarketDataFeed.Otc => "otc",
        _ => "iex",
    };

    private static BarTimeFrame MapBarSize(BarSize s) => s switch
    {
        BarSize.OneMinute => new BarTimeFrame(1, BarTimeFrameUnit.Minute),
        BarSize.ThreeMinutes => new BarTimeFrame(3, BarTimeFrameUnit.Minute),
        BarSize.FiveMinutes => new BarTimeFrame(5, BarTimeFrameUnit.Minute),
        BarSize.FifteenMinutes => new BarTimeFrame(15, BarTimeFrameUnit.Minute),
        BarSize.OneHour => new BarTimeFrame(1, BarTimeFrameUnit.Hour),
        BarSize.OneDay => new BarTimeFrame(1, BarTimeFrameUnit.Day),
        _ => new BarTimeFrame(1, BarTimeFrameUnit.Minute),
    };

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        _state.Dispose();
    }

    private enum AssetClass { Stock, Crypto, Other }
}
