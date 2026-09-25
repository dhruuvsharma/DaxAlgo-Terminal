using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Binance;
using TradingTerminal.Infrastructure.Bitfinex;
using TradingTerminal.Infrastructure.Bitget;
using TradingTerminal.Infrastructure.Bithumb;
using TradingTerminal.Infrastructure.Bitstamp;
using TradingTerminal.Infrastructure.Bitvavo;
using TradingTerminal.Infrastructure.Bybit;
using TradingTerminal.Infrastructure.Coinbase;
using TradingTerminal.Infrastructure.Crypto;
using TradingTerminal.Infrastructure.CryptoCom;
using TradingTerminal.Infrastructure.Deribit;
using TradingTerminal.Infrastructure.GateIo;
using TradingTerminal.Infrastructure.Gemini;
using TradingTerminal.Infrastructure.Htx;
using TradingTerminal.Infrastructure.Hyperliquid;
using TradingTerminal.Infrastructure.Kraken;
using TradingTerminal.Infrastructure.KuCoin;
using TradingTerminal.Infrastructure.Mexc;
using TradingTerminal.Infrastructure.Okx;
using TradingTerminal.Infrastructure.Upbit;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Every public crypto venue added on 2026-09-25, driven end to end against the real venue.
///
/// <para><b>Opt-in.</b> Runs only with <c>DAXALGO_LIVE_VENUES=1</c>; otherwise each case returns at once.
/// It needs the network and live markets, and a suite that fails when an exchange has a bad minute is a
/// suite people learn to ignore.</para>
///
/// <para>What it proves that the wire-format tests cannot: that the subscribe message is accepted, that
/// the keepalive keeps the socket open, and that what the venue sends today still parses. The wire-format
/// tests pin the shapes; this checks the shapes are still the venue's.</para>
/// </summary>
public sealed class PublicCryptoVenuesLiveRun(ITestOutputHelper output)
{
    private static readonly TimeSpan FirstItem = TimeSpan.FromSeconds(45);

    public static TheoryData<string> Venues() =>
    [
        "Bitget", "KuCoin", "GateIo", "Gemini", "CryptoCom", "Upbit",
        "Bithumb", "Bitfinex", "Bitstamp", "Bitvavo", "Htx", "Mexc",
    ];

    /// <summary>The venues that were here before 2026-09-25. Their parsers are lazy iterators, and the
    /// shared stream disposed each frame's JSON before reading them — so every stream but Binance's
    /// threw on its first frame. Run with the others so that stays fixed.</summary>
    public static TheoryData<string> EarlierVenues() =>
    [
        "Binance", "Coinbase", "Bybit", "Kraken", "Okx", "Deribit", "Hyperliquid",
    ];

    private static (IBrokerClient Client, Contract Contract) Create(string venue)
    {
        ILogger<T> Log<T>() => NullLogger<T>.Instance;
        IBrokerClient client = venue switch
        {
            "Bitget" => new RealBitgetClient(Log<RealBitgetClient>(), Options.Create(new BitgetOptions())),
            "KuCoin" => new RealKuCoinClient(Log<RealKuCoinClient>(), Options.Create(new KuCoinOptions())),
            "GateIo" => new RealGateIoClient(Log<RealGateIoClient>(), Options.Create(new GateIoOptions())),
            "Gemini" => new RealGeminiClient(Log<RealGeminiClient>(), Options.Create(new GeminiOptions())),
            "CryptoCom" => new RealCryptoComClient(Log<RealCryptoComClient>(), Options.Create(new CryptoComOptions())),
            "Upbit" => new RealUpbitClient(Log<RealUpbitClient>(), Options.Create(new UpbitOptions())),
            "Bithumb" => new RealBithumbClient(Log<RealBithumbClient>(), Options.Create(new BithumbOptions())),
            "Bitfinex" => new RealBitfinexClient(Log<RealBitfinexClient>(), Options.Create(new BitfinexOptions())),
            "Bitstamp" => new RealBitstampClient(Log<RealBitstampClient>(), Options.Create(new BitstampOptions())),
            "Bitvavo" => new RealBitvavoClient(Log<RealBitvavoClient>(), Options.Create(new BitvavoOptions())),
            "Htx" => new RealHtxClient(Log<RealHtxClient>(), Options.Create(new HtxOptions())),
            "Mexc" => new RealMexcClient(Log<RealMexcClient>(), Options.Create(new MexcOptions())),
            "Binance" => new RealBinanceClient(Log<RealBinanceClient>(), Options.Create(new BinanceOptions())),
            "Coinbase" => new RealCoinbaseClient(Log<RealCoinbaseClient>(), Options.Create(new CoinbaseOptions())),
            "Bybit" => new RealBybitClient(Log<RealBybitClient>(), Options.Create(new BybitOptions())),
            "Kraken" => new RealKrakenClient(Log<RealKrakenClient>(), Options.Create(new KrakenOptions())),
            "Okx" => new RealOkxClient(Log<RealOkxClient>(), Options.Create(new OkxOptions())),
            "Deribit" => new RealDeribitClient(Log<RealDeribitClient>(), Options.Create(new DeribitOptions())),
            "Hyperliquid" => new RealHyperliquidClient(Log<RealHyperliquidClient>(), Options.Create(new HyperliquidOptions())),
            _ => throw new ArgumentOutOfRangeException(nameof(venue)),
        };

        // The first default instrument is each venue's bitcoin market — the liveliest it has.
        var contract = client.ListInstrumentsAsync().GetAwaiter().GetResult()[0].Contract;
        return (client, contract);
    }

    [Theory]
    [MemberData(nameof(Venues))]
    [MemberData(nameof(EarlierVenues))]
    public async Task Every_data_path_delivers(string venue)
    {
        if (Environment.GetEnvironmentVariable("DAXALGO_LIVE_VENUES") != "1") return;

        var (client, contract) = Create(venue);
        await using var _ = client;

        ConnectionState? state = null;
        using (client.ConnectionState.Subscribe(s => state = s))
            await client.ConnectAsync();
        Assert.Equal(ConnectionState.Connected, state);

        // History, native and rolled up.
        var minutes = await client.RequestHistoricalBarsAsync(contract, BarSize.OneMinute, TimeSpan.FromHours(2));
        Report("history 1m", minutes.Count, minutes.LastOrDefault());
        AssertBars(minutes, TimeSpan.FromMinutes(1), minimum: 60);

        var threes = await client.RequestHistoricalBarsAsync(contract, BarSize.ThreeMinutes, TimeSpan.FromHours(3));
        Report("history 3m", threes.Count, threes.LastOrDefault());
        AssertBars(threes, TimeSpan.FromMinutes(3), minimum: 20);

        var days = await client.RequestHistoricalBarsAsync(contract, BarSize.OneDay, TimeSpan.FromDays(10));
        Report("history 1D", days.Count, days.LastOrDefault());
        Assert.NotEmpty(days);

        // Live streams.
        var tick = await First(client.SubscribeTicksAsync(contract, ct: default), "ticks");
        Assert.True(tick.Bid > 0 && tick.Ask > 0 && tick.Bid <= tick.Ask, $"crossed or empty L1: {tick}");

        var depth = await First(client.SubscribeDepthAsync(contract, 10), "depth");
        Assert.NotEmpty(depth.Bids);
        Assert.NotEmpty(depth.Asks);
        Assert.True(depth.BestBid <= depth.BestAsk, $"crossed book {depth.BestBid} / {depth.BestAsk}");
        Assert.True(depth.Bids.Zip(depth.Bids.Skip(1)).All(p => p.First.Price > p.Second.Price), "bids not descending");
        Assert.True(depth.Asks.Zip(depth.Asks.Skip(1)).All(p => p.First.Price < p.Second.Price), "asks not ascending");

        var trade = await First(client.SubscribeTradesAsync(contract), "trades");
        // Size is not asserted positive: sizes are scaled to integers (×1000 for crypto), so a 0.0001 BTC
        // print is legitimately a size of 0. The price is what proves the parse.
        Assert.True(trade.Price > 0 && trade.Size >= 0, $"empty trade {trade}");
        Assert.True(Math.Abs((trade.TimestampUtc - DateTime.UtcNow).TotalMinutes) < 5, $"trade time {trade.TimestampUtc:O} is not now");

        var bar = await First(client.SubscribeBarsAsync(contract, BarSize.OneMinute), "bars 1m");
        Assert.True(bar.High >= bar.Low && bar.Open > 0, $"malformed bar {bar}");

        var bar3 = await First(client.SubscribeBarsAsync(contract, BarSize.ThreeMinutes), "bars 3m");
        Assert.Equal(0, (bar3.TimestampUtc - DateTime.UnixEpoch).Ticks % TimeSpan.FromMinutes(3).Ticks);
    }

    public static TheoryData<BrokerKind> KeyedVenues() =>
    [
        BrokerKind.Binance, BrokerKind.Bybit, BrokerKind.Okx, BrokerKind.Kraken, BrokerKind.Deribit,
        BrokerKind.Coinbase, BrokerKind.Bitget, BrokerKind.KuCoin, BrokerKind.GateIo, BrokerKind.Gemini, BrokerKind.CryptoCom,
        BrokerKind.Upbit, BrokerKind.Bithumb, BrokerKind.Bitfinex, BrokerKind.Bitstamp, BrokerKind.Bitvavo,
        BrokerKind.Htx, BrokerKind.Mexc,
    ];

    /// <summary>
    /// A made-up key must be <b>refused</b> — in the venue's own words — and not accepted, and not
    /// mistaken for the venue being unreachable.
    ///
    /// <para>What that proves without an account: the signed request reaches each venue's authentication
    /// layer (a malformed request is usually refused for its shape, before the key is looked at), and the
    /// probe reads each venue's refusal correctly. What it cannot prove is that a <i>valid</i> key signs
    /// correctly — that needs a real key, which is why these venues stay Unverified.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(KeyedVenues))]
    public async Task A_made_up_key_is_refused_in_the_venues_own_words(BrokerKind venue)
    {
        if (Environment.GetEnvironmentVariable("DAXALGO_LIVE_VENUES") != "1") return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var throwaway = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var fake = venue == BrokerKind.Coinbase
            // Coinbase signs with an EC key, so the made-up key has to be a real (throwaway) one to sign at all.
            ? new BrokerCredential("organizations/daxalgo-probe/apiKeys/daxalgo-probe", throwaway.ExportECPrivateKeyPem())
            : new BrokerCredential(
                Key: "daxalgo-probe-00000000000000000000",
                // Kraken's secret is base64 and is decoded before signing.
                Secret: Convert.ToBase64String(new byte[64]),
                Passphrase: "daxalgo-probe");

        // Up to three tries for an answer: some edges (Bitstamp's, from some networks) drop TLS handshakes,
        // and this test is about reading the answer, not about the network.
        ProbeResult result = default;
        for (var attempt = 0; attempt < 3 && !result.Reached; attempt++)
        {
            result = await CryptoAccountProbe.ProbeAsync(http, venue, fake, DateTimeOffset.UtcNow);
            output.WriteLine($"{venue}: ok={result.Ok} reached={result.Reached} — {result.Detail}");
        }

        Assert.False(result.Ok, "a made-up key was accepted");
        Assert.True(result.Reached, $"no verdict: {result.Detail}");
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    private static void AssertBars(IReadOnlyList<Bar> bars, TimeSpan step, int minimum)
    {
        Assert.True(bars.Count >= minimum, $"only {bars.Count} bars");
        Assert.True(bars.Zip(bars.Skip(1)).All(p => p.First.TimestampUtc < p.Second.TimestampUtc), "bars not oldest-first");
        Assert.All(bars, b =>
        {
            Assert.Equal(0, (b.TimestampUtc - DateTime.UnixEpoch).Ticks % step.Ticks);
            Assert.True(b.Low <= b.Open && b.Open <= b.High && b.Low <= b.Close && b.Close <= b.High, $"OHLC out of order: {b}");
        });
        Assert.True(DateTime.UtcNow - bars[^1].TimestampUtc < step + step + TimeSpan.FromMinutes(5), $"newest bar {bars[^1].TimestampUtc:O} is stale");
    }

    private async Task<T> First<T>(IAsyncEnumerable<T> stream, string what)
    {
        using var cts = new CancellationTokenSource(FirstItem);
        var watch = Stopwatch.StartNew();
        try
        {
            await foreach (var item in stream.WithCancellation(cts.Token))
            {
                output.WriteLine($"{what}: first after {watch.ElapsedMilliseconds} ms — {item}");
                return item;
            }
        }
        catch (OperationCanceledException) { }

        throw new TimeoutException($"{what}: nothing within {FirstItem.TotalSeconds}s");
    }

    private void Report(string what, int count, Bar? newest) =>
        output.WriteLine($"{what}: {count} bars, newest {newest}");
}
