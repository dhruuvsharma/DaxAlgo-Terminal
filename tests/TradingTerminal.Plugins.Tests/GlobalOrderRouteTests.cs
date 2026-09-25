using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.AliceBlue;
using TradingTerminal.Infrastructure.AngelOne;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Dhan;
using TradingTerminal.Infrastructure.ETrade;
using TradingTerminal.Infrastructure.FivePaisa;
using TradingTerminal.Infrastructure.Fyers;
using TradingTerminal.Infrastructure.IciciBreeze;
using TradingTerminal.Infrastructure.Ig;
using TradingTerminal.Infrastructure.IronBeam;
using TradingTerminal.Infrastructure.Oanda;
using TradingTerminal.Infrastructure.Questrade;
using TradingTerminal.Infrastructure.RobinhoodCrypto;
using TradingTerminal.Infrastructure.Saxo;
using TradingTerminal.Infrastructure.Schwab;
using TradingTerminal.Infrastructure.Tastytrade;
using TradingTerminal.Infrastructure.TradeStation;
using TradingTerminal.Infrastructure.Tradier;
using TradingTerminal.Infrastructure.Tradovate;
using TradingTerminal.Infrastructure.Upstox;
using TradingTerminal.Infrastructure.Zerodha;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The US, global and Indian order routes: what each sends and how each reads the answer, against a recording
/// handler — plus the shared session keeper they depend on. None of this needs an account; what it cannot prove
/// is that the broker agrees, which is why every route is Unverified until it has placed an order.
/// </summary>
public sealed class GlobalOrderRouteTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>One broker's stored credentials; the session, when given, is a kept session good for an hour.</summary>
    private sealed class Store(BrokerCredential credential) : IBrokerCredentialSource, IBrokerSessionStore
    {
        public BrokerCredential Credential { get; set; } = credential;

        public List<string> Renewed { get; } = [];

        public BrokerCredential For(BrokerKind broker) => Credential;

        public void Renew(BrokerKind broker, string session)
        {
            Renewed.Add(session);
            Credential = Credential with { Session = session };
        }

        public static Store Kept(string access = "access-1", string server = "", string extra = "", string account = "", string key = "app-key", string secret = "app-secret") =>
            new(new BrokerCredential(key, secret)
            {
                Account = account,
                Extra = extra,
                Session = new KeptSession { AccessToken = access, RefreshToken = "refresh-1", ExpiresUtc = Now.AddHours(1), Server = server, Secret = "sec-1", Extra = extra }.ToJson(),
            });

        public static Store Daily(string key = "app-key", string secret = "app-secret", string session = "day-token", string account = "AB1234") =>
            new(new BrokerCredential(key, secret) { Session = session, Account = account });
    }

    /// <summary>Records each request and answers from a queue (or with <c>{}</c>).</summary>
    private sealed class Venue(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _answers = new(answers);

        public List<(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers)> Seen { get; } = [];

        public Func<HttpResponseMessage, HttpResponseMessage>? Shape { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.ToString(), body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase)));
            var (status, text) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}");
            var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            return Shape is null ? response : Shape(response);
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement Body(string text) => Json(text);

    private static RouteOrderRequest Order(string symbol, OrderSide side, decimal quantity, RouteOrderType type = RouteOrderType.Market,
        decimal? limit = null, decimal? stop = null, RouteTimeInForce tif = RouteTimeInForce.Day) =>
        new(symbol, "daxt-a1b2c3d4e5f6-7", side, type, tif, quantity, limit, stop);

    private static readonly NullLogger Log = NullLogger.Instance;

    // ── The shared session keeper ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_keeper_per_broker_and_store_so_a_rotating_refresh_token_is_spent_once()
    {
        var store = Store.Kept();
        store.Credential = store.Credential with
        {
            Session = new KeptSession { AccessToken = "old", RefreshToken = "refresh-1", ExpiresUtc = Now.AddSeconds(10) }.ToJson(),
        };
        var spent = new List<string>();
        Task<KeptSession> Renew(BrokerCredential app, KeptSession s, CancellationToken ct)
        {
            lock (spent) spent.Add(s.RefreshToken);
            return Task.FromResult(new KeptSession { AccessToken = "new", RefreshToken = "refresh-2", ExpiresUtc = Now.AddMinutes(30) });
        }

        var dataSide = SessionKeeper.Shared(BrokerKind.Questrade, store, store, Renew, Log, new Clock(Now));
        var orderSide = SessionKeeper.Shared(BrokerKind.Questrade, store, store, (_, _, _) => throw new InvalidOperationException("second renewal"), Log, new Clock(Now));
        orderSide.Should().BeSameAs(dataSide, "the market-data client and the order route renew one token between them");
        SessionKeeper.Shared(BrokerKind.SaxoBank, store, store, Renew, Log).Should().NotBeSameAs(dataSide);
        var other = Store.Kept();
        SessionKeeper.Shared(BrokerKind.Questrade, other, other, Renew, Log).Should().NotBeSameAs(dataSide, "each credential store keeps its own");

        var sessions = await Task.WhenAll(dataSide.CurrentAsync(CancellationToken.None), orderSide.CurrentAsync(CancellationToken.None));
        sessions.Should().OnlyContain(s => s.AccessToken == "new");
        spent.Should().Equal("refresh-1");
    }

    [Fact]
    public async Task A_route_asked_for_the_environment_its_session_does_not_reach_refuses_before_sending()
    {
        var venue = new Venue();
        using var route = new TastytradeOrderRoute(Store.Kept(), Store.Kept(), new TastytradeOptions { RestBaseUrl = "https://api.cert.tastyworks.com" },
            Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("SANDBOX").And.Contain("live account");
        venue.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task No_stored_session_is_a_refusal_to_sign_in_not_an_unknown_outcome()
    {
        var store = new Store(new BrokerCredential("k", "s"));
        using var route = new SchwabOrderRoute(store, store, new SchwabOptions(), Log, new Clock(Now), new Venue());

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Order("AAPL", OrderSide.Buy, 1), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("sign in");
    }

    // ── Tradier ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tradier_spells_out_a_short_sale_from_the_position_and_refuses_a_sell_through_zero()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"profile":{"account":{"account_number":"VA000001","status":"active"}}}"""),
            (HttpStatusCode.OK, """{"positions":"null"}"""),
            (HttpStatusCode.OK, """{"order":{"id":228175,"status":"ok"}}"""),
            (HttpStatusCode.OK, """{"positions":{"position":{"symbol":"AAPL","quantity":5}}}"""));
        using var route = new TradierOrderRoute(new Store(new BrokerCredential("", "token-1")), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Order("AAPL", OrderSide.Sell, 3, RouteOrderType.Limit, 190.5m), CancellationToken.None);

        order.OrderId.Should().Be("228175");
        var post = venue.Seen[2];
        post.Url.Should().Be("https://sandbox.tradier.com/v1/accounts/VA000001/orders");
        post.Headers["Authorization"].Should().Be("Bearer token-1");
        post.Body.Should().Contain("side=sell_short").And.Contain("type=limit").And.Contain("price=190.5").And.Contain("duration=day").And.Contain("class=equity");

        var crossing = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Paper, Order("AAPL", OrderSide.Sell, 8), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        crossing.Which.IsRejection.Should().BeTrue();
        crossing.Which.Message.Should().Contain("long to short");
        venue.Seen.Should().HaveCount(4, "the crossing order was never sent");
    }

    [Fact]
    public void Tradier_reads_a_list_that_is_an_object_an_array_or_the_string_null()
    {
        TradierOrderRoute.ReadPosition(Json("""{"positions":{"position":{"symbol":"AAPL","quantity":-2}}}"""), "AAPL").Should().Be(-2);
        TradierOrderRoute.ReadPosition(Json("""{"positions":{"position":[{"symbol":"AAPL","quantity":2},{"symbol":"MSFT","quantity":9}]}}"""), "AAPL").Should().Be(2);
        TradierOrderRoute.ReadPosition(Json("""{"positions":"null"}"""), "AAPL").Should().Be(0);
        TradierOrderRoute.Words(Json("""{"errors":{"error":["Backoffice rejected override of the order.","DayTradingBuyingPowerExceeded"]}}"""))
            .Should().Be("Backoffice rejected override of the order.; DayTradingBuyingPowerExceeded");
    }

    // ── OANDA ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Oanda_sends_a_sell_as_negative_units_fill_or_kill_and_reads_the_fill_from_the_answer()
    {
        var venue = new Venue((HttpStatusCode.Created, """
            {"orderCreateTransaction":{"id":"6356","type":"MARKET_ORDER"},
             "orderFillTransaction":{"id":"6357","orderID":"6356","units":"-1000","price":"1.10125","commission":"0.0000"}}
            """));
        using var route = new OandaOrderRoute(new Store(new BrokerCredential("101-001-1234567-001", "token-1")), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Order("EUR_USD", OrderSide.Sell, 1000), CancellationToken.None);

        var seen = venue.Seen.Single();
        seen.Url.Should().Be("https://api-fxpractice.oanda.com/v3/accounts/101-001-1234567-001/orders");
        var sent = Json(seen.Body).GetProperty("order");
        sent.GetProperty("units").GetString().Should().Be("-1000");
        sent.GetProperty("timeInForce").GetString().Should().Be("FOK", "OANDA takes a market order only FOK or IOC");
        sent.GetProperty("clientExtensions").GetProperty("id").GetString().Should().Be("daxt-a1b2c3d4e5f6-7");
        order.Should().Match<RouteOrder>(o => o.OrderId == "6356" && o.Status == RouteOrderStatus.Filled && o.FilledQuantity == 1000 && o.AveragePrice == 1.10125m);
    }

    [Fact]
    public async Task Oanda_names_the_environment_when_a_token_is_refused()
    {
        var venue = new Venue((HttpStatusCode.Unauthorized, """{"errorMessage":"Insufficient authorization to perform request."}"""));
        using var route = new OandaOrderRoute(new Store(new BrokerCredential("001-001-1-001", "t")), Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("Insufficient authorization").And.Contain("practice and live issue separate tokens");
        OandaOrderRoute.ReadPosition(Json("""{"position":{"long":{"units":"100"},"short":{"units":"-40"}}}""")).Should().Be(60);
    }

    // ── TradeStation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TradeStation_trades_the_sim_margin_account_with_the_intent_the_position_implies()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"Accounts":[{"AccountID":"SIM111F","AccountType":"Futures","Status":"Active"},{"AccountID":"SIM222M","AccountType":"Margin","Status":"Active"}]}"""),
            (HttpStatusCode.OK, """{"Positions":[{"Symbol":"MSFT","Quantity":"10","LongShort":"Short"}]}"""),
            (HttpStatusCode.OK, """{"Orders":[{"OrderID":"924243071","Message":"Sent order: Buy 4 MSFT @ Market"}]}"""));
        var store = Store.Kept();
        using var route = new TradeStationOrderRoute(store, store, new TradeStationOptions(), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Order("MSFT", OrderSide.Buy, 4), CancellationToken.None);

        order.OrderId.Should().Be("924243071");
        venue.Seen.Should().OnlyContain(s => s.Url.StartsWith("https://sim-api.tradestation.com/v3/", StringComparison.Ordinal));
        venue.Seen[0].Headers["Authorization"].Should().Be("Bearer access-1");
        var sent = Json(venue.Seen[2].Body);
        sent.GetProperty("AccountID").GetString().Should().Be("SIM222M");
        sent.GetProperty("TradeAction").GetString().Should().Be("BUYTOCOVER");
        sent.GetProperty("OrderType").GetString().Should().Be("Market");
        sent.GetProperty("TimeInForce").GetProperty("Duration").GetString().Should().Be("DAY");
        TradeStationOrderRoute.Status("FPR", 2).Should().Be(RouteOrderStatus.PartiallyFilled);
        TradeStationOrderRoute.Status("OUT", 0).Should().Be(RouteOrderStatus.Cancelled);
    }

    // ── Schwab ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Schwab_reads_the_new_order_id_from_Location_and_is_live_only()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """[{"accountNumber":"12345678","hashValue":"HASH-A"}]"""),
            (HttpStatusCode.OK, """{"securitiesAccount":{"positions":[]}}"""),
            (HttpStatusCode.Created, ""))
        {
            Shape = response =>
            {
                if (response.StatusCode == HttpStatusCode.Created)
                    response.Headers.Location = new Uri("https://api.schwabapi.com/trader/v1/accounts/HASH-A/orders/1000123");
                return response;
            },
        };
        var store = Store.Kept();
        using var route = new SchwabOrderRoute(store, store, new SchwabOptions(), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Order("AAPL", OrderSide.Buy, 2, RouteOrderType.Limit, 180m), CancellationToken.None);

        order.OrderId.Should().Be("1000123");
        venue.Seen[2].Url.Should().Be("https://api.schwabapi.com/trader/v1/accounts/HASH-A/orders");
        var leg = Json(venue.Seen[2].Body).GetProperty("orderLegCollection")[0];
        leg.GetProperty("instruction").GetString().Should().Be("BUY");
        (await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Paper, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.Message.Should().Contain("no paper environment");
    }

    [Fact]
    public void Schwab_weights_the_average_price_from_its_execution_legs()
    {
        var store = Store.Kept();
        using var route = new SchwabOrderRoute(store, store, new SchwabOptions(), Log, new Clock(Now), new Venue());
        var order = route.ReadOrder(Json("""
            {"orderId":1000123,"status":"WORKING","orderType":"LIMIT","duration":"DAY","quantity":10,"filledQuantity":4,"price":180,
             "orderLegCollection":[{"instruction":"BUY","instrument":{"symbol":"AAPL"}}],
             "orderActivityCollection":[{"activityType":"EXECUTION","executionLegs":[{"quantity":1,"price":179.5},{"quantity":3,"price":180}]}]}
            """), "AAPL");
        order.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.PartiallyFilled && o.FilledQuantity == 4 && o.AveragePrice == 179.875m);
    }

    // ── Saxo ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saxo_places_a_stop_limit_with_its_trigger_as_order_price_and_an_idempotency_id()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"ClientKey":"CK","DefaultAccountKey":"AK","DefaultAccountId":"9226397"}"""),
            (HttpStatusCode.OK, """{"OrderId":"76289286"}"""));
        var store = Store.Kept();
        using var route = new SaxoOrderRoute(store, store, new SaxoOptions { RestBaseUrl = "https://gateway.saxobank.com/sim/openapi" }, Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper,
            Order("FxSpot:21", OrderSide.Sell, 10000, RouteOrderType.StopLimit, limit: 1.0990m, stop: 1.1000m), CancellationToken.None);

        order.OrderId.Should().Be("76289286");
        var post = venue.Seen[1];
        post.Url.Should().Be("https://gateway.saxobank.com/sim/openapi/trade/v2/orders");
        post.Headers["x-request-id"].Should().Be("daxt-a1b2c3d4e5f6-7");
        var sent = Json(post.Body);
        sent.GetProperty("Uic").GetInt64().Should().Be(21);
        sent.GetProperty("OrderPrice").GetDecimal().Should().Be(1.1000m);
        sent.GetProperty("StopLimitPrice").GetDecimal().Should().Be(1.0990m);
        sent.GetProperty("ManualOrder").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Saxo_reads_where_an_order_ended_from_its_last_audit_activity()
    {
        var store = Store.Kept();
        using var route = new SaxoOrderRoute(store, store, new SaxoOptions(), Log, new Clock(Now), new Venue());
        var order = route.ReadActivities(Json("""
            {"Data":[
              {"OrderId":"5","Status":"Placed","Amount":10000,"BuySell":"Buy","OrderType":"Limit","ActivityTime":"2026-09-25T10:00:00Z"},
              {"OrderId":"5","Status":"Fill","FillAmount":4000,"ExecutionPrice":1.1,"Amount":10000,"BuySell":"Buy","ActivityTime":"2026-09-25T10:00:01Z"},
              {"OrderId":"5","Status":"FinalFill","FillAmount":6000,"ExecutionPrice":1.2,"Amount":10000,"BuySell":"Buy","ActivityTime":"2026-09-25T10:00:02Z"}]}
            """), "FxSpot:EURUSD", "5");
        order!.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.Filled && o.FilledQuantity == 10000 && o.AveragePrice == 1.16m);
    }

    // ── IG ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ig_refuses_a_resting_limit_and_reads_a_market_deal_from_its_confirmation()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"instrument":{"epic":"CS.D.EURUSD.CFD.IP","expiry":"-","valueOfOnePip":"10.00","currencies":[{"code":"USD","isDefault":true}]},"dealingRules":{"minDealSize":{"value":0.5}},"snapshot":{"decimalPlacesFactor":1,"bid":11000.1,"offer":11000.9}}"""),
            (HttpStatusCode.OK, """{"instrument":{"epic":"CS.D.EURUSD.CFD.IP","expiry":"-","valueOfOnePip":"10.00","currencies":[{"code":"USD","isDefault":true}]},"dealingRules":{"minDealSize":{"value":0.5}},"snapshot":{"decimalPlacesFactor":1}}"""),
            (HttpStatusCode.OK, """{"dealReference":"daxt-a1b2c3d4e5f6-7"}"""),
            (HttpStatusCode.OK, """{"dealReference":"daxt-a1b2c3d4e5f6-7","dealId":"DIAAAA","dealStatus":"ACCEPTED","reason":"SUCCESS","direction":"BUY","size":1,"level":11001.2,"date":"2026-09-25T12:00:00"}"""));
        var store = Store.Kept(extra: "demo", server: "https://demo-api.ig.com/gateway/deal");
        using var route = new IgOrderRoute(store, store, new IgOptions(), Log, new Clock(Now), venue);

        (await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Paper, Order("CS.D.EURUSD.CFD.IP", OrderSide.Buy, 1, RouteOrderType.Limit, 11000m), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>()).Which.Message.Should().Contain("working order");
        venue.Seen.Should().BeEmpty();

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Order("CS.D.EURUSD.CFD.IP", OrderSide.Buy, 1), CancellationToken.None);
        order.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.Filled && o.AveragePrice == 11001.2m && o.ClientOrderId == "daxt-a1b2c3d4e5f6-7");
        var deal = venue.Seen[2];
        deal.Url.Should().Be("https://demo-api.ig.com/gateway/deal/positions/otc");
        deal.Headers["Version"].Should().Be("2");
        deal.Headers["CST"].Should().Be("access-1");
        Json(deal.Body).GetProperty("timeInForce").GetString().Should().Be("FILL_OR_KILL");
        Json(deal.Body).GetProperty("forceOpen").GetBoolean().Should().BeFalse();
    }

    // ── Tradovate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tradovate_calls_a_rate_limit_ticket_a_refusal_and_builds_an_order_from_three_records()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """[{"id":7,"name":"DEMO7","active":true}]"""),
            (HttpStatusCode.OK, """{"p-ticket":"abc","p-time":5}"""));
        var store = Store.Kept(extra: "demo");
        using var route = new TradovateOrderRoute(store, store, new TradovateOptions(), Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Paper, Order("ESZ6", OrderSide.Buy, 1), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("rate limited");
        var sent = Json(venue.Seen[1].Body);
        venue.Seen[1].Url.Should().Be("https://demo.tradovateapi.com/v1/order/placeorder");
        sent.GetProperty("isAutomated").GetBoolean().Should().BeTrue();
        sent.GetProperty("accountSpec").GetString().Should().Be("DEMO7");

        var order = TradovateOrderRoute.ReadOrder(
            Json("""{"id":55,"action":"Sell","ordStatus":"Working","timestamp":"2026-09-25T11:59:00Z"}"""),
            Json("""{"id":1,"orderId":55,"orderQty":3,"orderType":"Limit","price":5000.25,"timeInForce":"GTC"}"""),
            Json("""[{"orderId":55,"qty":1,"price":5000.25,"active":true},{"orderId":55,"qty":1,"price":5001.25,"active":false},{"orderId":99,"qty":5,"price":1}]"""),
            "ESZ6");
        order.Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.PartiallyFilled && o.FilledQuantity == 1 && o.AveragePrice == 5000.25m && o.Side == OrderSide.Sell);
    }

    // ── E*TRADE ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ETrade_previews_then_places_the_same_order_with_the_preview_id()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"AccountListResponse":{"Accounts":{"Account":[{"accountId":"84376","accountIdKey":"KEY84","institutionType":"BROKERAGE","accountStatus":"ACTIVE"}]}}}"""),
            (HttpStatusCode.NoContent, ""),
            (HttpStatusCode.OK, """{"PreviewOrderResponse":{"PreviewIds":[{"previewId":1938}]}}"""),
            (HttpStatusCode.OK, """{"PlaceOrderResponse":{"OrderIds":[{"orderId":529}]}}"""));
        var store = Store.Kept();
        using var route = new ETradeOrderRoute(store, store, new ETradeOptions(), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Order("IBM", OrderSide.Buy, 5, RouteOrderType.Limit, 150m), CancellationToken.None);

        order.OrderId.Should().Be("529");
        venue.Seen[2].Url.Should().Be("https://api.etrade.com/v1/accounts/KEY84/orders/preview.json");
        venue.Seen[3].Url.Should().Be("https://api.etrade.com/v1/accounts/KEY84/orders/place.json");
        venue.Seen[3].Headers["Authorization"].Should().StartWith("OAuth ").And.Contain("oauth_token=\"access-1\"");
        var place = Json(venue.Seen[3].Body).GetProperty("PlaceOrderRequest");
        place.GetProperty("PreviewIds")[0].GetProperty("previewId").GetInt64().Should().Be(1938);
        place.GetProperty("clientOrderId").GetString().Should().Be("daxta1b2c3d4e5f67");
        place.GetProperty("Order")[0].GetProperty("Instrument")[0].GetProperty("orderAction").GetString().Should().Be("BUY");

        using var sandbox = new ETradeOrderRoute(store, store, new ETradeOptions { RestBaseUrl = "https://apisb.etrade.com" }, Log, new Clock(Now), new Venue());
        (await FluentActions.Awaiting(() => sandbox.ConnectAsync(RouteEnvironment.Live, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.Message.Should().Contain("canned data");
    }

    // ── Robinhood, Questrade, Ironbeam ───────────────────────────────────────────────────────────

    [Fact]
    public void Robinhood_names_the_order_config_after_its_type_and_sends_a_uuid_client_id()
    {
        var body = RobinhoodCryptoOrderRoute.SubmitBody(Order("BTC-USD", OrderSide.Buy, 0.001m, RouteOrderType.Limit, 60000m, tif: RouteTimeInForce.GoodTillCancelled),
            "0b5cda1a-54fb-4c0c-8a8a-6f3f5b3a6a3e");
        body["type"]!.GetValue<string>().Should().Be("limit");
        body["limit_order_config"]!["asset_quantity"]!.GetValue<string>().Should().Be("0.001");
        body["limit_order_config"]!["time_in_force"]!.GetValue<string>().Should().Be("gtc");
        body["client_order_id"]!.GetValue<string>().Should().Be("0b5cda1a-54fb-4c0c-8a8a-6f3f5b3a6a3e");
    }

    [Fact]
    public async Task Questrade_sends_orders_to_the_server_its_session_names()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"accounts":[{"number":"26598145","status":"Active","isPrimary":true}]}"""),
            (HttpStatusCode.OK, """{"symbols":[{"symbol":"RY.TO","symbolId":34658}]}"""),
            (HttpStatusCode.Forbidden, """{"code":1003,"message":"Access to this API is restricted to partner applications"}"""));
        var store = Store.Kept(server: "https://api07.iq.questrade.com/");
        using var route = new QuestradeOrderRoute(store, store, new QuestradeOptions(), Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Order("RY.TO", OrderSide.Buy, 1), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("partner applications");
        venue.Seen[2].Url.Should().Be("https://api07.iq.questrade.com/v1/accounts/26598145/orders");
        Json(venue.Seen[2].Body).GetProperty("symbolId").GetInt64().Should().Be(34658);
    }

    [Fact]
    public async Task Ironbeam_sends_the_order_type_and_duration_codes_its_api_defines()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"token":"jwt-1","status":"OK"}"""),
            (HttpStatusCode.OK, """{"accounts":["5123345"],"status":"OK"}"""),
            (HttpStatusCode.OK, """{"orderId":"5123345-1","strategyId":42,"status":"OK"}"""));
        using var route = new IronBeamOrderRoute(new Store(new BrokerCredential("user-1", "key-1") { Extra = "demo" }), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Paper, Order("XCME:ES.Z26", OrderSide.Sell, 2, RouteOrderType.StopLimit, 4999m, 5000m, RouteTimeInForce.GoodTillCancelled), CancellationToken.None);

        order.OrderId.Should().Be("5123345-1");
        venue.Seen[0].Url.Should().Be("https://demo.ironbeamapi.com/v2/auth");
        venue.Seen[2].Url.Should().Be("https://demo.ironbeamapi.com/v2/order/5123345/place");
        venue.Seen[2].Headers["Authorization"].Should().Be("Bearer jwt-1");
        var sent = Json(venue.Seen[2].Body);
        sent.GetProperty("orderType").GetString().Should().Be("4");
        sent.GetProperty("duration").GetString().Should().Be("1");
        sent.GetProperty("side").GetString().Should().Be("SELL");
        (await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.Message.Should().Contain("demo gateway");
    }

    // ── India ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zerodha_places_a_delivery_order_with_the_engine_id_as_its_tag()
    {
        var venue = new Venue((HttpStatusCode.OK, """{"status":"success","data":{"order_id":"151220000000000"}}"""));
        using var route = new ZerodhaOrderRoute(Store.Daily(key: "kite-key", session: "kite-token"), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Order("NSE:INFY", OrderSide.Buy, 3, RouteOrderType.Stop, stop: 1500m), CancellationToken.None);

        order.OrderId.Should().Be("151220000000000");
        var seen = venue.Seen.Single();
        seen.Url.Should().Be("https://api.kite.trade/orders/regular");
        seen.Headers["Authorization"].Should().Be("token kite-key:kite-token");
        seen.Body.Should().Contain("tradingsymbol=INFY").And.Contain("exchange=NSE").And.Contain("product=CNC").And.Contain("order_type=SL-M")
            .And.Contain("trigger_price=1500").And.Contain("tag=daxta1b2c3d4e5f67");
    }

    [Fact]
    public void Zerodha_reads_times_as_indian_and_the_position_as_holding_plus_the_days_delivery_trades()
    {
        using var route = new ZerodhaOrderRoute(Store.Daily(), Log, new Clock(Now), new Venue());
        var order = route.ReadOrder(Json("""{"order_id":"1","status":"COMPLETE","transaction_type":"BUY","order_type":"MARKET","quantity":3,"filled_quantity":3,"average_price":1501.5,"exchange_update_timestamp":"2026-09-25 15:00:00","tag":"x"}"""), "NSE:INFY");
        order.UpdatedAtUtc.Should().Be(new DateTime(2026, 9, 25, 9, 30, 0, DateTimeKind.Utc));
        order.Status.Should().Be(RouteOrderStatus.Filled);
        ZerodhaOrderRoute.ReadPosition(
            Json("""[{"exchange":"NSE","tradingsymbol":"INFY","quantity":10,"t1_quantity":2}]"""),
            Json("""{"day":[{"exchange":"NSE","tradingsymbol":"INFY","product":"CNC","quantity":-4},{"exchange":"NSE","tradingsymbol":"INFY","product":"MIS","quantity":7}]}"""),
            "NSE:INFY").Should().Be(8);
    }

    [Fact]
    public async Task No_indian_broker_trades_on_paper_or_without_the_days_session()
    {
        using var zerodha = new ZerodhaOrderRoute(Store.Daily(), Log, new Clock(Now), new Venue());
        (await FluentActions.Awaiting(() => zerodha.ConnectAsync(RouteEnvironment.Paper, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.IsRejection.Should().BeTrue();
        using var dhan = new DhanOrderRoute(Store.Daily(session: ""), Log, new Clock(Now), new Venue());
        (await FluentActions.Awaiting(() => dhan.ConnectAsync(RouteEnvironment.Live, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.Message.Should().Contain("sign in for today");
    }

    [Fact]
    public void Upstox_writes_the_instrument_key_with_its_bar_and_matches_the_price_by_token()
    {
        UpstoxOrderRoute.Key("NSE_EQ:INE002A01018").Should().Be("NSE_EQ|INE002A01018");
        var body = UpstoxOrderRoute.SubmitBody(Order("NSE_EQ|INE002A01018", OrderSide.Sell, 2, RouteOrderType.Limit, 2900.5m), "tag-1");
        body["instrument_token"]!.GetValue<string>().Should().Be("NSE_EQ|INE002A01018");
        body["product"]!.GetValue<string>().Should().Be("D");
        UpstoxOrderRoute.ReadPrice(Json("""{"NSE_EQ:RELIANCE":{"instrument_token":"NSE_EQ|INE002A01018","last_price":2901.1}}"""), "NSE_EQ|INE002A01018", Now.UtcDateTime)!
            .Price.Should().Be(2901.1m);
    }

    [Fact]
    public async Task AngelOne_takes_the_trading_symbol_from_the_quote_and_sends_stops_as_the_stoploss_variety()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """{"status":true,"data":{"fetched":[{"exchange":"NSE","tradingSymbol":"RELIANCE-EQ","symbolToken":"2885","ltp":2900.5}]}}"""),
            (HttpStatusCode.OK, """{"status":true,"message":"SUCCESS","data":{"orderid":"201020000000080","uniqueorderid":"u-1"}}"""));
        using var route = new AngelOneOrderRoute(Store.Daily(key: "smart-key", session: """{"jwt":"jwt-1","feed":"f"}"""), Log, new Clock(Now), venue);

        var order = await route.SubmitAsync(RouteEnvironment.Live, Order("NSE:2885", OrderSide.Buy, 1, RouteOrderType.StopLimit, 2910m, 2905m), CancellationToken.None);

        order.OrderId.Should().Be("201020000000080");
        venue.Seen[1].Headers["Authorization"].Should().Be("Bearer jwt-1");
        venue.Seen[1].Headers["X-PrivateKey"].Should().Be("smart-key");
        var sent = Json(venue.Seen[1].Body);
        sent.GetProperty("tradingsymbol").GetString().Should().Be("RELIANCE-EQ");
        sent.GetProperty("variety").GetString().Should().Be("STOPLOSS");
        sent.GetProperty("ordertype").GetString().Should().Be("STOPLOSS_LIMIT");
        sent.GetProperty("producttype").GetString().Should().Be("DELIVERY");
    }

    [Fact]
    public async Task AngelOne_reads_an_invalid_token_answered_with_success_false_under_200_as_a_refusal()
    {
        // The live answer to a made-up JWT, 2026-09-25: the flag is "success", not "status", and the code is "errorCode".
        var venue = new Venue((HttpStatusCode.OK, """{"success":false,"message":"Invalid Token","errorCode":"AG8001","data":""}"""));
        using var route = new AngelOneOrderRoute(Store.Daily(session: """{"jwt":"made-up","feed":""}"""), Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("Invalid Token (AG8001)");
        AngelAnswer.IsRefusal(Json("""{"status":false,"message":"Order rejected","errorcode":"AB1004"}""")).Should().BeTrue();
        AngelAnswer.IsRefusal(Json("""{"status":true,"message":"SUCCESS","data":{}}""")).Should().BeFalse();
    }

    [Fact]
    public async Task Dhan_refusal_is_read_from_its_error_body()
    {
        var venue = new Venue((HttpStatusCode.BadRequest, """{"errorType":"Order_Error","errorCode":"DH-906","errorMessage":"Insufficient Funds"}"""));
        using var route = new DhanOrderRoute(Store.Daily(account: "1000000001"), Log, new Clock(Now), venue);

        var refused = await FluentActions.Awaiting(() => route.SubmitAsync(RouteEnvironment.Live, Order("NSE_EQ:2885", OrderSide.Buy, 1), CancellationToken.None))
            .Should().ThrowAsync<BrokerOrderRouteException>();
        refused.Which.IsRejection.Should().BeTrue();
        refused.Which.Message.Should().Contain("Insufficient Funds (DH-906)");
        venue.Seen[0].Headers["client-id"].Should().Be("1000000001");
        var sent = Json(venue.Seen[0].Body);
        sent.GetProperty("securityId").GetString().Should().Be("2885");
        sent.GetProperty("exchangeSegment").GetString().Should().Be("NSE_EQ");
        sent.GetProperty("productType").GetString().Should().Be("CNC");
        sent.GetProperty("correlationId").GetString().Should().Be("daxt-a1b2c3d4e5f6-7");
    }

    [Fact]
    public void Fyers_numbers_its_order_types_and_statuses()
    {
        var body = FyersOrderRoute.SubmitBody(Order("NSE:SBIN-EQ", OrderSide.Sell, 5, RouteOrderType.Stop, stop: 800m), "tag");
        body["type"]!.GetValue<int>().Should().Be(3);
        body["side"]!.GetValue<int>().Should().Be(-1);
        using var route = new FyersOrderRoute(Store.Daily(), Log, new Clock(Now), new Venue());
        route.ReadOrder(Json("""{"id":"1","symbol":"NSE:SBIN-EQ","qty":5,"filledQty":5,"type":2,"side":1,"status":2,"tradedPrice":801.1,"orderDateTime":"25-Sep-2026 10:15:02"}"""), "NSE:SBIN-EQ")
            .Should().Match<RouteOrder>(o => o.Status == RouteOrderStatus.Filled && o.AveragePrice == 801.1m && o.UpdatedAtUtc == new DateTime(2026, 9, 25, 4, 45, 2, DateTimeKind.Utc));
    }

    [Fact]
    public void FivePaisa_counts_only_the_days_change_in_a_delivery_position_the_holding_already_holds()
    {
        FivePaisaOrderRoute.ReadPosition(
            Json("""{"Data":[{"Exch":"N","ExchType":"C","NseCode":4963,"Quantity":5}]}"""),
            Json("""{"NetPositionDetail":[{"Exch":"N","ExchType":"C","ScripCode":4963,"OrderFor":"D","BodQty":5,"NetQty":7}]}"""),
            "N:C:4963").Should().Be(7);
        FivePaisaOrderRoute.MicrosoftDate("/Date(1707279047230+0530)/").Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1707279047230).UtcDateTime);
        var body = FivePaisaOrderRoute.SubmitBody(Order("N:C:1660", OrderSide.Buy, 1), "remote1");
        body["Price"]!.GetValue<string>().Should().Be("0", "a zero price is 5paisa's market order");
        body["IsIntraday"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task AliceBlue_reads_a_refusal_from_a_200_and_an_empty_book_as_empty()
    {
        var venue = new Venue(
            (HttpStatusCode.OK, """[{"stat":"Not_Ok","emsg":"No Data"}]"""),
            (HttpStatusCode.OK, """{"stat":"Not_Ok","emsg":"Session Expired"}"""));
        using var route = new AliceBlueOrderRoute(Store.Daily(account: "ab123"), Log, new Clock(Now), venue);

        (await route.OrdersAsync(RouteEnvironment.Live, "NSE:2885", CancellationToken.None)).Should().BeEmpty();
        venue.Seen[0].Headers["Authorization"].Should().Be("Bearer AB123 day-token");
        (await FluentActions.Awaiting(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None)).Should().ThrowAsync<BrokerOrderRouteException>())
            .Which.Message.Should().Contain("Session Expired");
    }

    [Fact]
    public void IciciBreeze_counts_a_fill_only_where_a_status_says_one_happened()
    {
        using var route = new IciciBreezeOrderRoute(Store.Daily(), Log, new Clock(Now), new Venue());
        route.ReadOrder(Json("""{"order_id":"1","status":"Cancelled","quantity":"10","pending_quantity":"0","action":"Buy","order_type":"Limit"}"""), "NSE:RELIND")
            .FilledQuantity.Should().Be(0, "a cancelled order's zero pending quantity is not a fill");
        route.ReadOrder(Json("""{"order_id":"1","status":"Partially Executed","quantity":"10","pending_quantity":"6","average_price":"2900.5","action":"Buy"}"""), "NSE:RELIND")
            .Should().Match<RouteOrder>(o => o.FilledQuantity == 4 && o.Status == RouteOrderStatus.PartiallyFilled);
        FluentActions.Invoking(() => IciciBreezeOrderRoute.SubmitBody(Order("NSE:RELIND", OrderSide.Sell, 1, RouteOrderType.Stop, stop: 2800m), "r"))
            .Should().Throw<BrokerOrderRouteException>().Which.IsRejection.Should().BeTrue();
    }

    // ── Composition ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_order_route_is_registered_once_and_only_brokers_with_a_real_test_environment_offer_paper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddInfrastructureCore();
        services.AddCredentialedBrokers();
        using var provider = services.BuildServiceProvider();

        var routes = provider.GetServices<IBrokerOrderRoute>().ToList();
        routes.Select(r => r.RouteId).Should().OnlyHaveUniqueItems();
        routes.Select(r => r.Broker).Should().OnlyHaveUniqueItems();
        routes.Should().HaveCount(38);
        routes.Where(r => r.PaperEnvironmentName is null).Select(r => r.Broker).Should().Contain(
        [
            BrokerKind.CharlesSchwab, BrokerKind.ETrade, BrokerKind.RobinhoodCrypto, BrokerKind.Zerodha, BrokerKind.Upstox, BrokerKind.AngelOne,
            BrokerKind.Dhan, BrokerKind.Fyers, BrokerKind.FivePaisa, BrokerKind.AliceBlue, BrokerKind.IciciBreeze,
        ]);
        routes.Where(r => r.PaperEnvironmentName is not null).Select(r => r.Broker).Should().Contain(
        [
            BrokerKind.Tradier, BrokerKind.Oanda, BrokerKind.TradeStation, BrokerKind.Tastytrade, BrokerKind.Tradovate, BrokerKind.SaxoBank,
            BrokerKind.IgGroup, BrokerKind.Questrade, BrokerKind.IronBeam,
        ]);
    }
}
