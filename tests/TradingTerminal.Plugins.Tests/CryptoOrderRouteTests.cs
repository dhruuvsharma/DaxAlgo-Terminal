using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Binance;
using TradingTerminal.Infrastructure.Bitvavo;
using TradingTerminal.Infrastructure.Bybit;
using TradingTerminal.Infrastructure.Coinbase;
using TradingTerminal.Infrastructure.Deribit;
using TradingTerminal.Infrastructure.Gemini;
using TradingTerminal.Infrastructure.Htx;
using TradingTerminal.Infrastructure.Kraken;
using TradingTerminal.Infrastructure.Okx;
using TradingTerminal.Infrastructure.Upbit;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The crypto order routes: what each sends — host, signature inputs, order vocabulary — and how each reads
/// the answer, against a recording handler. None of this needs an account; what it cannot prove is that the
/// venue agrees, which is why every route is Unverified until it has placed an order.
/// </summary>
public sealed class CryptoOrderRouteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Keys(BrokerCredential credential) : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) => credential;
    }

    /// <summary>Records each request and answers from a queue (or with <c>{}</c>).</summary>
    private sealed class Venue(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _answers = new(answers);

        public List<(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.ToString(), body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase)));
            var (status, text) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static RouteOrderRequest Limit(string symbol, decimal quantity, decimal price, RouteTimeInForce tif = RouteTimeInForce.GoodTillCancelled) =>
        new(symbol, "daxt-a1b2c3d4e5f6-7", OrderSide.Buy, RouteOrderType.Limit, tif, quantity, price, null);

    private static RouteOrderRequest Market(string symbol, decimal quantity) =>
        new(symbol, "daxt-a1b2c3d4e5f6-8", OrderSide.Buy, RouteOrderType.Market, RouteTimeInForce.GoodTillCancelled, quantity, null, null);

    // ── Binance ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Binance_signs_the_exact_query_it_sends_and_uses_the_testnet_for_paper()
    {
        var venue = new Venue((HttpStatusCode.OK, """{"symbol":"BTCUSDT","orderId":28,"clientOrderId":"daxt-a1b2c3d4e5f6-7","transactTime":1790000000000,"origQty":"0.00030000","executedQty":"0.00000000","cummulativeQuoteQty":"0","status":"NEW","timeInForce":"GTC","type":"LIMIT","side":"BUY","price":"50000.00","fills":[]}"""));
        using var route = new BinanceOrderRoute(new Keys(new BrokerCredential("key-1", "secret-1")), NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Limit("BTCUSDT", 0.0003m, 50000m), CancellationToken.None);

        var seen = venue.Seen.Single();
        seen.Url.Should().StartWith("https://testnet.binance.vision/api/v3/order?");
        seen.Headers["X-MBX-APIKEY"].Should().Be("key-1");
        var query = new Uri(seen.Url).Query.TrimStart('?');
        var signed = query[..query.LastIndexOf("&signature=", StringComparison.Ordinal)];
        query.Should().EndWith("&signature=" + Convert.ToHexStringLower(HMACSHA256.HashData("secret-1"u8.ToArray(), Encoding.UTF8.GetBytes(signed))));
        signed.Should().Contain("type=LIMIT").And.Contain("timeInForce=GTC").And.Contain("quantity=0.0003").And.Contain("price=50000")
            .And.Contain("newClientOrderId=daxt-a1b2c3d4e5f6-7");
        order.Should().Match<RouteOrder>(o => o.OrderId == "28" && o.ClientOrderId == "daxt-a1b2c3d4e5f6-7" && o.Status == RouteOrderStatus.Working);
    }

    [Fact]
    public async Task Binance_tells_a_refusal_from_its_own_timeout()
    {
        var venue = new Venue(
            (HttpStatusCode.BadRequest, """{"code":-2010,"msg":"Account has insufficient balance for requested action."}"""),
            (HttpStatusCode.BadRequest, """{"code":-1007,"msg":"Timeout waiting for response from backend server. Send status unknown; execution status unknown."}"""));
        using var route = new BinanceOrderRoute(new Keys(new BrokerCredential("k", "s")), NullLogger.Instance, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Limit("BTCUSDT", 1m, 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("insufficient balance");

        var unknown = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Limit("BTCUSDT", 1m, 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        unknown.Which.IsRejection.Should().BeFalse("the order may exist: Binance says so");
    }

    [Fact]
    public void Binance_rules_come_from_the_filters_and_fees_are_reported_in_the_coin_they_were_taken_in()
    {
        using var route = new BinanceOrderRoute(new Keys(BrokerCredential.None), NullLogger<BinanceOrderRoute>.Instance);
        var rules = route.ReadInstrument(Json("""{"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","orderTypes":["LIMIT","LIMIT_MAKER","MARKET","STOP_LOSS_LIMIT"],"filters":[{"filterType":"PRICE_FILTER","tickSize":"0.01000000"},{"filterType":"LOT_SIZE","minQty":"0.00001000","maxQty":"9000.00000000","stepSize":"0.00001000"}]}]}"""), "BTCUSDT")!;
        rules.Should().Match<RouteInstrument>(r => r.UnitSize == 0.00001m && r.TickSize == 0.01m && r.MinimumUnits == 1 && r.BaseAsset == "BTC" && r.Currency == "USDT");
        rules.OrderTypes.Should().Be(RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit);

        BinanceShapedOrderRoute.SumFees(Json("""[{"commission":"0.00000030","commissionAsset":"BTC"},{"commission":"0.00000010","commissionAsset":"BTC"}]"""), "BTC", "USDT")
            .Should().Be((0.0000004m, "BTC"));
        BinanceShapedOrderRoute.SumFees(Json("""[{"commission":"0.0001","commissionAsset":"BNB"}]"""), "BTC", "USDT").Should().Be((0.0001m, "BNB"));
    }

    // ── Bybit ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bybit_sizes_a_market_buy_in_the_base_coin_and_reads_a_refusal_from_retCode()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"retCode":0,"retMsg":"OK","result":{"orderId":"1321003749386327552","orderLinkId":"daxt-a1b2c3d4e5f6-8"}}"""),
            (HttpStatusCode.OK, """{"retCode":170131,"retMsg":"Insufficient balance.","result":{}}"""));
        using var route = new BybitOrderRoute(new Keys(new BrokerCredential("bk", "bs")), NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Market("BTCUSDT", 0.001m), CancellationToken.None);
        order.OrderId.Should().Be("1321003749386327552");
        var sent = venue.Seen[0];
        sent.Url.Should().Be("https://api-testnet.bybit.com/v5/order/create");
        using (var body = JsonDocument.Parse(sent.Body))
        {
            body.RootElement.GetProperty("marketUnit").GetString().Should().Be("baseCoin");
            body.RootElement.GetProperty("qty").GetString().Should().Be("0.001");
        }
        sent.Headers["X-BAPI-SIGN"].Should().Be(TradingTerminal.Infrastructure.Crypto.CryptoAuth.BybitSignature(
            sent.Headers["X-BAPI-TIMESTAMP"], "bk", "5000", sent.Body, "bs"));

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Paper, Market("BTCUSDT", 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("Insufficient balance");
    }

    [Fact]
    public void Bybit_reads_cumulative_value_and_the_fee_coin()
    {
        using var route = new BybitOrderRoute(new Keys(BrokerCredential.None), NullLogger<BybitOrderRoute>.Instance);
        var order = route.ReadOrders(Json("""{"list":[{"orderId":"9","orderLinkId":"x","symbol":"BTCUSDT","side":"Buy","orderType":"Limit","price":"50000","qty":"0.002","timeInForce":"GTC","orderStatus":"PartiallyFilled","cumExecQty":"0.001","cumExecValue":"50.01","avgPrice":"0","cumExecFee":"0.000001","updatedTime":"1790000000000"}]}""")).Single();
        order.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.PartiallyFilled && o.FilledQuantity == 0.001m && o.AveragePrice == 50010m
            && o.Fee == 0.000001m && o.FeeCurrency == "BTC");
    }

    // ── OKX ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Okx_demo_carries_the_simulated_header_and_a_32_character_client_id()
    {
        var venue = new Venue((HttpStatusCode.OK, """{"code":"0","msg":"","data":[{"ordId":"312269865356374016","clOrdId":"x","sCode":"0","sMsg":""}]}"""));
        using var route = new OkxOrderRoute(new Keys(new BrokerCredential("ok", "os", "pass")), NullLogger.Instance, new Clock(Now), venue);

        await route.SubmitAsync(RouteEnvironment.Paper, Market("BTC-USDT", 0.001m), CancellationToken.None);

        var sent = venue.Seen.Single();
        sent.Headers["x-simulated-trading"].Should().Be("1");
        sent.Headers["OK-ACCESS-PASSPHRASE"].Should().Be("pass");
        using var body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("tgtCcy").GetString().Should().Be("base_ccy");
        body.RootElement.GetProperty("tdMode").GetString().Should().Be("cash");
        body.RootElement.GetProperty("clOrdId").GetString().Should().MatchRegex("^[A-Za-z0-9]{1,32}$");
    }

    [Fact]
    public async Task Okx_reports_the_per_order_reason_when_the_envelope_only_says_failed()
    {
        var venue = new Venue((HttpStatusCode.OK, """{"code":"1","msg":"Operation failed.","data":[{"ordId":"","sCode":"51008","sMsg":"Order failed. Insufficient USDT balance in account."}]}"""));
        using var route = new OkxOrderRoute(new Keys(new BrokerCredential("k", "s", "p")), NullLogger.Instance, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Market("BTC-USDT", 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.Message.Should().Contain("Insufficient USDT balance").And.Contain("51008");
        refused.Which.IsRejection.Should().BeTrue();
    }

    // ── Kraken ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kraken_sends_the_altname_with_rising_nonces_and_reads_errors_from_a_200()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"error":[],"result":{"descr":{"order":"buy 0.001 XBTUSD @ limit 50000"},"txid":["OUF4EM-FRGI2-MQMWZD"]}}"""),
            (HttpStatusCode.OK, """{"error":["EOrder:Insufficient funds"]}"""),
            (HttpStatusCode.OK, """{"error":["EService:Unavailable"]}"""));
        using var route = new KrakenOrderRoute(new Keys(new BrokerCredential("kk", Convert.ToBase64String("kraken-secret"u8.ToArray()))),
            NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Limit("BTC/USD", 0.001m, 50000m), CancellationToken.None);
        order.OrderId.Should().Be("OUF4EM-FRGI2-MQMWZD");
        venue.Seen[0].Body.Should().Contain("pair=XBTUSD").And.Contain("ordertype=limit").And.Contain("volume=0.001");

        (await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Limit("BTC/USD", 1m, 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>()).Which.IsRejection.Should().BeTrue();
        (await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Limit("BTC/USD", 1m, 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>()).Which.IsRejection.Should().BeFalse("a service failure says nothing about the order");

        var nonces = venue.Seen.Select(s => long.Parse(s.Body.Split('&')[0]["nonce=".Length..], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        nonces.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("a frozen clock still yields rising nonces");
        KrakenOrderRoute.Altname("DOGE/USD").Should().Be("XDGUSD");
    }

    // ── Coinbase ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coinbase_describes_each_order_by_its_configuration_and_reads_a_refusal_in_a_200()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ec.ExportECPrivateKeyPem();
        var venue = new Venue(
            (HttpStatusCode.OK, """{"success":true,"success_response":{"order_id":"11111-00000-000000","product_id":"BTC-USD","side":"BUY","client_order_id":"x"}}"""),
            (HttpStatusCode.OK, """{"success":false,"failure_reason":"UNKNOWN_FAILURE_REASON","error_response":{"error":"INSUFFICIENT_FUND","message":"Insufficient balance in source account"}}"""));
        using var route = new CoinbaseOrderRoute(new Keys(new BrokerCredential("organizations/x/apiKeys/y", pem)), NullLogger.Instance, new Clock(Now), venue);

        await route.SubmitAsync(RouteEnvironment.Live, Limit("BTC-USD", 0.001m, 50000m, RouteTimeInForce.ImmediateOrCancel), CancellationToken.None);
        using (var body = JsonDocument.Parse(venue.Seen[0].Body))
        {
            var ioc = body.RootElement.GetProperty("order_configuration").GetProperty("sor_limit_ioc");
            ioc.GetProperty("base_size").GetString().Should().Be("0.001");
            ioc.GetProperty("limit_price").GetString().Should().Be("50000");
        }
        venue.Seen[0].Headers["Authorization"].Should().StartWith("Bearer ey");

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Market("BTC-USD", 1m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.Message.Should().Contain("Insufficient balance");
        refused.Which.IsRejection.Should().BeTrue();
    }

    // ── Deribit ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deribit_trades_on_a_client_credentials_token_and_labels_orders_with_the_engine_id()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"access_token":"tok","expires_in":900,"token_type":"bearer"}}"""),
            (HttpStatusCode.OK, """{"jsonrpc":"2.0","result":{"order":{"order_id":"ETH-349","label":"daxt-a1b2c3d4e5f6-7","instrument_name":"BTC-PERPETUAL","direction":"buy","order_type":"limit","time_in_force":"good_til_cancelled","amount":10,"price":50000,"order_state":"open","filled_amount":0,"average_price":0,"commission":0,"last_update_timestamp":1790000000000},"trades":[]}}"""));
        using var route = new DeribitOrderRoute(new Keys(new BrokerCredential("client", "secret")), NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Limit("BTC-PERPETUAL", 10m, 50000m), CancellationToken.None);

        venue.Seen[0].Url.Should().StartWith("https://test.deribit.com/api/v2/public/auth?grant_type=client_credentials");
        venue.Seen[1].Url.Should().StartWith("https://test.deribit.com/api/v2/private/buy?").And.Contain("label=daxt-a1b2c3d4e5f6-7").And.Contain("amount=10");
        venue.Seen[1].Headers["Authorization"].Should().Be("Bearer tok");
        order.Should().Match<RouteOrder>(o => o.ClientOrderId == "daxt-a1b2c3d4e5f6-7" && o.Status == RouteOrderStatus.Working);

        var inverse = DeribitOrderRoute.ReadInstrument(Json("""{"instrument_name":"BTC-PERPETUAL","tick_size":0.5,"min_trade_amount":10,"contract_size":10,"quote_currency":"USD","instrument_type":"reversed"}"""), 50000m)!;
        inverse.ValuePerPoint.Should().Be(0.0002m, "ten dollars of an inverse contract is worth 10 / 50 000 per point at 50 000");
    }

    // ── Gemini, Upbit, Bitvavo, HTX ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_carries_the_whole_request_in_the_payload_header()
    {
        var venue = new Venue((HttpStatusCode.OK, """{"order_id":"106817811","client_order_id":"x","symbol":"btcusd","side":"buy","type":"exchange limit","price":"50000","avg_execution_price":"0","is_live":true,"is_cancelled":false,"executed_amount":"0","original_amount":"0.001","options":["immediate-or-cancel"],"timestampms":1790000000000}"""));
        using var route = new GeminiOrderRoute(new Keys(new BrokerCredential("gk", "gs")), NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Limit("BTCUSD", 0.001m, 50000m, RouteTimeInForce.ImmediateOrCancel), CancellationToken.None);

        var sent = venue.Seen.Single();
        sent.Url.Should().Be("https://api.sandbox.gemini.com/v1/order/new");
        using var payload = JsonDocument.Parse(Convert.FromBase64String(sent.Headers["X-GEMINI-PAYLOAD"]));
        payload.RootElement.GetProperty("request").GetString().Should().Be("/v1/order/new");
        payload.RootElement.GetProperty("symbol").GetString().Should().Be("btcusd");
        payload.RootElement.GetProperty("options")[0].GetString().Should().Be("immediate-or-cancel");
        sent.Headers["X-GEMINI-SIGNATURE"].Should().Be(Convert.ToHexStringLower(HMACSHA384.HashData("gs"u8.ToArray(), Encoding.UTF8.GetBytes(sent.Headers["X-GEMINI-PAYLOAD"]))));
        order.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.Working && o.TimeInForce == RouteTimeInForce.ImmediateOrCancel);
    }

    [Fact]
    public async Task Upbit_hashes_the_order_parameters_into_its_token()
    {
        var venue = new Venue((HttpStatusCode.Created, """{"uuid":"cdd92199-2897-4e14-9448-f923320408ad","side":"bid","ord_type":"limit","price":"100000000","state":"wait","market":"KRW-BTC","volume":"0.001","executed_volume":"0","paid_fee":"0","identifier":"x","created_at":"2026-09-25T21:00:00+09:00"}"""));
        using var route = new UpbitOrderRoute(new Keys(new BrokerCredential("uk", "us")), NullLogger.Instance, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Limit("KRW-BTC", 0.001m, 100_000_000m), CancellationToken.None);

        var sent = venue.Seen.Single();
        var claims = JsonDocument.Parse(Base64Url(sent.Headers["Authorization"]["Bearer ".Length..].Split('.')[1])).RootElement;
        var parameters = string.Join('&', JsonDocument.Parse(sent.Body).RootElement.EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}"));
        claims.GetProperty("query_hash").GetString().Should().Be(Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(parameters))));
        claims.GetProperty("query_hash_alg").GetString().Should().Be("SHA512");
        order.Should().Match<RouteOrder>(o => o.OrderId == "cdd92199-2897-4e14-9448-f923320408ad" && o.FeeCurrency == "KRW");
        UpbitShapedOrderRoute.KrwTick(143_000_000m).Should().Be(1000m);
    }

    private static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }

    [Fact]
    public async Task Bitvavo_client_ids_are_uuids_mapped_back_to_the_engine_id()
    {
        var venue = new Venue((HttpStatusCode.OK, "{}"));
        using var route = new BitvavoOrderRoute(new Keys(new BrokerCredential("vk", "vs")), NullLogger.Instance, new Clock(Now), venue);
        await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Market("BTC-EUR", 0.001m), CancellationToken.None)).Should().NotThrowAsync();

        using var body = JsonDocument.Parse(venue.Seen.Single().Body);
        var uuid = body.RootElement.GetProperty("clientOrderId").GetString()!;
        Guid.TryParse(uuid, out _).Should().BeTrue();
        route.ReadOrder(Json($$"""{"orderId":"1","clientOrderId":"{{uuid}}","market":"BTC-EUR","status":"filled","side":"buy","orderType":"market","amount":"0.001","filledAmount":"0.001","filledAmountQuote":"50.5","feePaid":"0.1","feeCurrency":"EUR","updated":1790000000000}"""))
            .Should().Match<RouteOrder>(o => o.ClientOrderId == "daxt-a1b2c3d4e5f6-8" && o.AveragePrice == 50500m && o.Status == RouteOrderStatus.Filled);
    }

    [Fact]
    public async Task Htx_orders_name_the_spot_account_read_at_connect()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"status":"ok","data":[{"id":100009,"type":"margin","state":"working"},{"id":100001,"type":"spot","state":"working"}]}"""),
            (HttpStatusCode.OK, """{"status":"ok","data":{"id":100001,"type":"spot","list":[{"currency":"usdt","type":"trade","balance":"91.85"},{"currency":"usdt","type":"frozen","balance":"8.15"}]}}"""),
            (HttpStatusCode.OK, """{"status":"ok","data":"59378"}"""));
        using var route = new HtxOrderRoute(new Keys(new BrokerCredential("hk", "hs")), NullLogger.Instance, new Clock(Now), venue);

        var account = await route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None);
        account.Should().Be(new RouteAccount("100001", "usdt", 100m, 91.85m));
        var order = await route.SubmitAsync(RouteEnvironment.Live, Limit("btcusdt", 0.001m, 50000m, RouteTimeInForce.ImmediateOrCancel), CancellationToken.None);
        order.OrderId.Should().Be("59378");
        using var body = JsonDocument.Parse(venue.Seen[2].Body);
        body.RootElement.GetProperty("account-id").GetString().Should().Be("100001");
        body.RootElement.GetProperty("type").GetString().Should().Be("buy-ioc");
        venue.Seen[2].Url.Should().Contain("AccessKeyId=hk").And.Contain("SignatureVersion=2").And.Contain("&Signature=");
    }

    // ── Composition ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_crypto_route_is_registered_once_under_a_bounded_stable_id()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddInfrastructureCore();
        services.AddCredentialedBrokers();
        using var provider = services.BuildServiceProvider();

        var routes = provider.GetServices<IBrokerOrderRoute>().ToList();
        routes.Select(r => r.RouteId).Should().OnlyHaveUniqueItems();
        routes.Should().OnlyContain(r => r.RouteId.Length <= 64 && r.RouteId == r.RouteId.ToLowerInvariant());
        routes.Select(r => r.Broker).Should().Contain(
        [
            BrokerKind.Binance, BrokerKind.Bybit, BrokerKind.Okx, BrokerKind.Kraken, BrokerKind.Coinbase, BrokerKind.Deribit,
            BrokerKind.Bitget, BrokerKind.KuCoin, BrokerKind.GateIo, BrokerKind.Gemini, BrokerKind.CryptoCom, BrokerKind.Upbit,
            BrokerKind.Bithumb, BrokerKind.Bitfinex, BrokerKind.Bitstamp, BrokerKind.Bitvavo, BrokerKind.Htx, BrokerKind.Mexc,
        ]);
        BrokerKind[] crypto =
        [
            BrokerKind.Binance, BrokerKind.Bybit, BrokerKind.Okx, BrokerKind.Kraken, BrokerKind.Coinbase, BrokerKind.Deribit,
            BrokerKind.Bitget, BrokerKind.KuCoin, BrokerKind.GateIo, BrokerKind.Gemini, BrokerKind.CryptoCom, BrokerKind.Upbit,
            BrokerKind.Bithumb, BrokerKind.Bitfinex, BrokerKind.Bitstamp, BrokerKind.Bitvavo, BrokerKind.Htx, BrokerKind.Mexc,
        ];
        routes.Where(r => r.PaperEnvironmentName is not null && crypto.Contains(r.Broker)).Select(r => r.Broker).Should().BeEquivalentTo(
            [BrokerKind.Binance, BrokerKind.Bybit, BrokerKind.Okx, BrokerKind.Deribit, BrokerKind.Gemini, BrokerKind.CryptoCom],
            "only venues with a real test environment offer PAPER");
    }
}
