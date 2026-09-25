using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Every order route against its real broker, with no account.
///
/// <para><b>What it proves.</b> The instrument rules and a price are read from the live venue through the
/// route's own parser — so the response shapes the route assumes are the shapes the venue sends — and a
/// connect with made-up credentials comes back refused in the broker's own words, which proves the host,
/// the path and the authentication headers reach the broker's auth layer. What it cannot prove is that an
/// order is accepted; that takes an account.</para>
///
/// <para><b>Opt-in.</b> Runs only with <c>DAXALGO_LIVE_VENUES=1</c>. One request per call, no orders.</para>
/// </summary>
public sealed class OrderRouteLiveRun(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DAXALGO_LIVE_VENUES") == "1";

    private sealed class MadeUp : IBrokerCredentialSource
    {
        private static readonly string Pem = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportECPrivateKeyPem();

        /// <summary>A kept session that looks fresh, so the first call goes out with it instead of renewing.</summary>
        private static string Kept(string server = "") => new Infrastructure.Brokers.KeptSession
        {
            AccessToken = "daxalgo-made-up-access", RefreshToken = "daxalgo-made-up-refresh", Secret = "daxalgo-made-up-secret",
            Server = server, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        }.ToJson();

        public BrokerCredential For(BrokerKind broker) => broker switch
        {
            // Coinbase signs with an EC key, Kraken's secret is base64: made up, but well formed.
            BrokerKind.Coinbase => new BrokerCredential("organizations/daxalgo/apiKeys/made-up", Pem),
            BrokerKind.Kraken => new BrokerCredential("daxalgo-made-up-key", Convert.ToBase64String(new byte[64])),
            // Robinhood signs with an Ed25519 seed: 32 bytes, base64.
            BrokerKind.RobinhoodCrypto => new BrokerCredential("daxalgo-made-up-key", Convert.ToBase64String(new byte[32])),
            BrokerKind.Oanda => new BrokerCredential("001-001-1234567-001", "daxalgo-made-up-token"),
            BrokerKind.IronBeam => new BrokerCredential("1234567", "daxalgo-made-up-key") { Extra = "live" },
            BrokerKind.Questrade => new BrokerCredential("", "") { Session = Kept("https://api01.iq.questrade.com/") },
            BrokerKind.CharlesSchwab or BrokerKind.TradeStation or BrokerKind.Tastytrade or BrokerKind.ETrade or BrokerKind.Tradovate
                or BrokerKind.SaxoBank or BrokerKind.IgGroup =>
                new BrokerCredential("daxalgo-made-up-key", "daxalgo-made-up-secret") { Session = Kept() },
            BrokerKind.AngelOne or BrokerKind.Zerodha or BrokerKind.Upstox or BrokerKind.Dhan or BrokerKind.Fyers or BrokerKind.FivePaisa
                or BrokerKind.AliceBlue or BrokerKind.IciciBreeze =>
                new BrokerCredential("daxalgo-made-up-key", "daxalgo-made-up-secret") { Session = "daxalgo-made-up-session", Account = "AB1234" },
            _ => new BrokerCredential("daxalgo-made-up-key", "daxalgo-made-up-secret", "daxalgo-made-up-passphrase"),
        };
    }

    /// <summary>The routes whose every call needs an account — a made-up session or key is all the live run can send.</summary>
    public static TheoryData<string> AccountRoutes => new()
    {
        "tradier", "oanda", "charles-schwab", "tradestation", "tastytrade", "etrade", "tradovate", "saxo-bank", "ig", "questrade",
        "robinhood-crypto", "ironbeam", "zerodha", "upstox", "angel-one", "dhan", "fyers", "5paisa", "alice-blue", "icici-breeze",
    };

    [Theory]
    [MemberData(nameof(AccountRoutes))]
    public async Task A_made_up_session_or_key_is_refused_by_the_broker_itself(string routeId)
    {
        if (!Enabled) return;
        var route = Route(routeId);
        var error = await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None))
            .Should().ThrowAsync<Exception>();
        output.WriteLine($"{routeId}: {error.Which.GetType().Name}: {error.Which.Message}");
        error.Which.Should().BeOfType<BrokerOrderRouteException>("a refusal, not a transport failure");
        error.Which.Message.Should().NotContain("HTTP 404", "a 404 means the path is wrong, not the key");
    }

    /// <summary>A symbol each venue lists, written the way its market-data client writes it.</summary>
    public static TheoryData<string, string> Routes => new()
    {
        { "binance", "BTCUSDT" }, { "bybit", "BTCUSDT" }, { "okx", "BTC-USDT" }, { "kraken", "BTC/USD" }, { "coinbase", "BTC-USD" },
        { "deribit", "BTC-PERPETUAL" }, { "bitget", "BTCUSDT" }, { "kucoin", "BTC-USDT" }, { "gate-io", "BTC_USDT" }, { "gemini", "BTCUSD" },
        { "crypto-com", "BTC_USD" }, { "upbit", "KRW-BTC" }, { "bithumb", "KRW-BTC" }, { "bitfinex", "BTCUSD" }, { "bitstamp", "btcusd" },
        { "bitvavo", "BTC-EUR" }, { "htx", "btcusdt" }, { "mexc", "BTCUSDT" },
    };

    private static IBrokerOrderRoute Route(string routeId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddInfrastructureCore();
        services.AddCredentialedBrokers();
        services.AddSingleton<IBrokerCredentialSource>(new MadeUp());
        var provider = services.BuildServiceProvider();
        return provider.GetServices<IBrokerOrderRoute>().Single(r => r.RouteId == routeId);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Rules_and_price_parse_from_the_live_venue(string routeId, string symbol)
    {
        if (!Enabled) return;
        var route = Route(routeId);
        foreach (var environment in route.PaperEnvironmentName is null ? [RouteEnvironment.Live] : new[] { RouteEnvironment.Live, RouteEnvironment.Paper })
        {
            // The private instrument endpoints (Upbit's order chance, for one) are not used for rules, so a
            // made-up key does not stop this; a venue whose public calls still sign is reported, not hidden.
            try
            {
                var rules = await route.InstrumentAsync(environment, symbol, CancellationToken.None);
                output.WriteLine($"{routeId} {environment}: unit {rules.UnitSize}, tick {rules.TickSize}, min {rules.MinimumUnits}, types {rules.OrderTypes}, {rules.BaseAsset}/{rules.Currency}");
                rules.IsValid.Should().BeTrue();
                var price = await route.PriceAsync(environment, symbol, CancellationToken.None);
                output.WriteLine($"{routeId} {environment}: price {price?.Price}");
                price.Should().NotBeNull();
                price!.Price.Should().BePositive();
            }
            catch (BrokerOrderRouteException exception) when (environment == RouteEnvironment.Paper)
            {
                output.WriteLine($"{routeId} {environment}: {exception.Message}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_made_up_key_is_refused_in_the_brokers_words(string routeId, string symbol)
    {
        if (!Enabled) return;
        _ = symbol;
        var route = Route(routeId);
        var error = await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None))
            .Should().ThrowAsync<Exception>();
        output.WriteLine($"{routeId}: {error.Which.GetType().Name}: {error.Which.Message}");
        error.Which.Should().BeOfType<BrokerOrderRouteException>("a refusal, not a transport failure");
        error.Which.Message.Should().NotContain("HTTP 404", "a 404 means the path is wrong, not the key");
    }
}
