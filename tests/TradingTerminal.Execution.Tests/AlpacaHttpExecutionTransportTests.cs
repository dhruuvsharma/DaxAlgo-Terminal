using System.Net;
using System.Text;
using System.Text.Json;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class AlpacaHttpExecutionTransportTests
{
    private const string KeyId = "in-process-key";
    private const string SecretKey = "in-process-secret";

    [Fact]
    public async Task ProductionTransport_UsesExactGatedEndpointsHeadersAndDecimalJson()
    {
        var handler = new DeterministicAlpacaHandler();
        await using var transport = new AlpacaHttpExecutionTransport(
            Endpoint(),
            TimeSpan.FromSeconds(2),
            handler);
        await transport.ConnectAsync(KeyId, SecretKey);

        var account = await transport.GetAccountAsync();
        var asset = await transport.GetAssetAsync("AAPL");
        var latest = await transport.GetLatestTradeAsync("AAPL");
        var submitted = await transport.SubmitOrderAsync(new AlpacaSubmitRequest(
            "AAPL",
            "client-1",
            "buy",
            "stop_limit",
            "day",
            new ScaledQuantity(12_345, 3),
            new ScaledPrice(101_375, 3),
            new ScaledPrice(99_625, 3)));
        await transport.CancelOrderAsync("order-1");
        var replaced = await transport.ReplaceOrderAsync(
            "order-1",
            new AlpacaReplaceRequest(
                new ScaledQuantity(7_125, 3),
                "gtc",
                new ScaledPrice(102_875, 3),
                new ScaledPrice(98_375, 3)));
        var byId = await transport.GetOrderByIdAsync("order-1");
        var byClient = await transport.GetOrderByClientIdAsync("client-1");
        var open = await transport.GetOrdersAsync(AlpacaOrderStatusFilter.Open);
        var positions = await transport.GetPositionsAsync();

        Assert.True(account.IsExecutionAuthorized);
        Assert.Equal(new ScaledMoney(100_055, 2), account.Cash);
        Assert.Equal(new ScaledMoney(200_075, 2), account.BuyingPower);
        Assert.True(asset.Fractionable);
        Assert.Equal(new ScaledQuantity(1, 3), asset.MinimumOrderSize);
        Assert.Equal(new ScaledQuantity(1, 4), asset.MinimumTradeIncrement);
        Assert.Equal(new ScaledPrice(1, 2), asset.PriceIncrement);
        Assert.Equal(new ScaledPrice(100_125, 3), latest!.Price);
        Assert.Equal(new ScaledQuantity(1_234, 3), submitted.FilledQuantity);
        Assert.Equal(new ScaledPrice(100_125, 3), submitted.FilledAveragePrice);
        Assert.Equal("order-2", replaced.OrderId);
        Assert.Equal("order-1", byId!.OrderId);
        Assert.Equal("client-1", byClient!.ClientOrderId);
        Assert.Single(open);
        Assert.Equal(new ScaledQuantity(-25, 1), Assert.Single(positions).Quantity);

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(KeyId, request.KeyId);
            Assert.Equal(SecretKey, request.SecretKey);
        });
        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://paper-api.alpaca.markets/v2/account");
        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://data.alpaca.markets/v2/stocks/AAPL/trades/latest");
        Assert.Contains(handler.Requests, request =>
            request.Uri == "https://paper-api.alpaca.markets/v2/orders?status=open&limit=500&direction=desc&nested=false");

        var submit = Assert.Single(handler.Requests, request => request.Method == "POST");
        using (var payload = JsonDocument.Parse(submit.Body!))
        {
            Assert.Equal("12.345", payload.RootElement.GetProperty("qty").GetString());
            Assert.Equal("101.375", payload.RootElement.GetProperty("limit_price").GetString());
            Assert.Equal("99.625", payload.RootElement.GetProperty("stop_price").GetString());
            Assert.Equal("client-1", payload.RootElement.GetProperty("client_order_id").GetString());
        }

        var replace = Assert.Single(handler.Requests, request => request.Method == "PATCH");
        using var replacement = JsonDocument.Parse(replace.Body!);
        Assert.Equal("7.125", replacement.RootElement.GetProperty("qty").GetString());
        Assert.Equal("102.875", replacement.RootElement.GetProperty("limit_price").GetString());
        Assert.Equal("98.375", replacement.RootElement.GetProperty("stop_price").GetString());
    }

    [Fact]
    public async Task ProductionTransport_RefusesResponseWhoseFinalUriEscapedToLive()
    {
        var handler = new EscapedFinalUriHandler();
        await using var transport = new AlpacaHttpExecutionTransport(
            Endpoint(),
            TimeSpan.FromSeconds(2),
            handler);
        await transport.ConnectAsync(KeyId, SecretKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.GetAccountAsync());

        Assert.Contains("escaped", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    private static AlpacaExecutionEndpoint Endpoint() =>
        AlpacaExecutionEndpointGate.Resolve(new AlpacaExecutionOptions
        {
            Enabled = true,
            BaseUrl = AlpacaExecutionOptions.PaperBaseUrl,
            MarketDataBaseUrl = AlpacaExecutionOptions.DataBaseUrl,
        });

    private sealed class DeterministicAlpacaHandler : HttpMessageHandler
    {
        internal List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                body,
                Assert.Single(request.Headers.GetValues("APCA-API-KEY-ID")),
                Assert.Single(request.Headers.GetValues("APCA-API-SECRET-KEY"))));

            var route = $"{request.Method.Method} {request.RequestUri.AbsoluteUri}";
            var response = route switch
            {
                "GET https://paper-api.alpaca.markets/v2/account" => Json(AccountJson),
                "GET https://paper-api.alpaca.markets/v2/assets/AAPL" => Json(AssetJson),
                "GET https://data.alpaca.markets/v2/stocks/AAPL/trades/latest" => Json(LatestTradeJson),
                "POST https://paper-api.alpaca.markets/v2/orders" => Json(OrderJson("order-1", "accepted")),
                "DELETE https://paper-api.alpaca.markets/v2/orders/order-1" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "PATCH https://paper-api.alpaca.markets/v2/orders/order-1" => Json(OrderJson("order-2", "new")),
                "GET https://paper-api.alpaca.markets/v2/orders/order-1" => Json(OrderJson("order-1", "partially_filled")),
                "GET https://paper-api.alpaca.markets/v2/orders:by_client_order_id?client_order_id=client-1" => Json(OrderJson("order-1", "partially_filled")),
                "GET https://paper-api.alpaca.markets/v2/orders?status=open&limit=500&direction=desc&nested=false" =>
                    Json($"[{OrderJson("order-1", "partially_filled")}]") ,
                "GET https://paper-api.alpaca.markets/v2/positions" => Json(PositionsJson),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected Alpaca route: {route}"),
            };
            response.RequestMessage = request;
            return response;
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        private static string OrderJson(string orderId, string status) => $$"""
            {
              "id":"{{orderId}}",
              "client_order_id":"client-1",
              "symbol":"AAPL",
              "asset_class":"us_equity",
              "side":"buy",
              "type":"stop_limit",
              "time_in_force":"day",
              "status":"{{status}}",
              "qty":"12.345",
              "filled_qty":"1.234",
              "filled_avg_price":"100.125",
              "limit_price":"101.375",
              "stop_price":"99.625",
              "updated_at":"2026-08-06T12:00:00Z",
              "reject_reason":null
            }
            """;

        private const string AccountJson = """
            {
              "id":"paper-account-1",
              "status":"ACTIVE",
              "currency":"USD",
              "cash":"1000.55",
              "buying_power":"2000.75",
              "trading_blocked":false,
              "account_blocked":false,
              "trade_suspended_by_user":false
            }
            """;

        private const string AssetJson = """
            {
              "symbol":"AAPL",
              "class":"us_equity",
              "tradable":true,
              "fractionable":true,
              "min_order_size":"0.001",
              "min_trade_increment":"0.0001",
              "price_increment":"0.01"
            }
            """;

        private const string LatestTradeJson = """
            {"trade":{"p":"100.125","t":"2026-08-06T12:00:01Z"}}
            """;

        private const string PositionsJson = """
            [{"symbol":"AAPL","asset_class":"us_equity","qty":"-2.5"}]
            """;
    }

    private sealed class EscapedFinalUriHandler : HttpMessageHandler
    {
        internal int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.alpaca.markets/v2/account"),
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Uri,
        string? Body,
        string KeyId,
        string SecretKey);
}
