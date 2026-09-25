using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.FivePaisa;

/// <summary>
/// 5paisa Xstream orders, with the app key, client code and the day's access token the login window stores.
/// Every call is a POST of <c>{"head":{"key"},"body":{…}}</c> with the token as a bearer.
///
/// <para>Symbols are the market-data client's <c>EXCH:TYPE:SCRIPCODE</c> (<c>N:C:2885</c>). Orders are delivery
/// (<c>IsIntraday: false</c>); a zero price is a market order, and <c>StopLossPrice</c> is the trigger. The
/// engine's id rides as <c>RemoteOrderID</c>. The order book reports <c>TradedQty</c> and <c>AveragePrice</c>;
/// a cancel needs the <i>exchange's</i> order id, which the route reads from the book. The day's net-wise
/// position carries the start-of-day delivery quantity (<c>BodQty</c>) that the holding also counts, so only
/// the day's change is added to the holding. Shapes are from 5paisa's Xstream documentation, read
/// 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed partial class FivePaisaOrderRoute : IndianOrderRoute
{
    private const string Root = "https://Openapi.5paisa.com/VendorsAPI/Service1.svc";

    public FivePaisaOrderRoute(IBrokerCredentialSource credentials, ILogger<FivePaisaOrderRoute> logger)
        : base(credentials, logger) { }

    internal FivePaisaOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.FivePaisa;
    public override string DisplayName => "5paisa";
    public override string RouteId => "5paisa";

    private string ClientCode => Credential.Account.Trim() is { Length: > 0 } code
        ? code
        : throw new BrokerOrderRouteException("5paisa: no client code is stored — sign in in the login window.", isRejection: true);

    /// <summary><c>N:C:2885</c> → (<c>N</c>, <c>C</c>, <c>2885</c>).</summary>
    internal static (string Exch, string Type, string Scrip) Parts(string symbol)
    {
        var parts = symbol.Trim().Split(':');
        return parts.Length >= 3 ? (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), parts[2]) : ("N", "C", parts[^1]);
    }

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, string path, JsonObject body, CancellationToken ct)
    {
        RequireLive(environment);
        var token = Session;
        var payload = new JsonObject { ["head"] = new JsonObject { ["key"] = Credential.Key.Trim() }, ["body"] = body }.ToJsonString();
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/{path}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");
            return request;
        }, ct).ConfigureAwait(false);
        var head = root.TryGetProperty("head", out var h) ? h : default;
        var answer = root.TryGetProperty("body", out var b) ? b : default;
        if (status is < 200 or >= 300 || (Str(head, "status") is { Length: > 0 } code && code != "0"))
            throw Refused(status, Str(head, "statusDescription") is { Length: > 0 } words ? words : Str(answer, "Message"), text,
                status is >= 200 and < 300 ? true : null);
        return answer;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var body = await CallAsync(environment, "V4/Margin", new JsonObject { ["ClientCode"] = ClientCode }, ct).ConfigureAwait(false);
        var margin = body.TryGetProperty("EquityMargin", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] : default;
        return new RouteAccount(ClientCode, "INR", Dec(margin, "Ledgerbalance"), Dec(margin, "NetAvailableMargin"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"5paisa does not know {symbol} — write it EXCH:TYPE:SCRIPCODE, e.g. N:C:2885.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = await CallAsync(environment, "V1/PlaceOrderRequest",
            SubmitBody(request, Token(request.ClientOrderId, id => Alphanumeric(id, 20))), ct).ConfigureAwait(false);
        if ((int)Dec(body, "Status") != 0)
            throw RefusedInBody(Str(body, "Message"), body.GetRawText());
        var id = Str(body, "BrokerOrderID");
        if (id.Length == 0 || id == "0")
            throw new InvalidDataException($"5paisa answered the order without an id: {SignInProof.Snippet(body.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string remoteId)
    {
        var (exch, type, scrip) = Parts(request.Symbol);
        return new JsonObject
        {
            ["Exchange"] = exch,
            ["ExchangeType"] = type,
            ["ScripCode"] = scrip,
            // A zero price is a market order; a stop's trigger is StopLossPrice.
            ["Price"] = Num(request.Type is RouteOrderType.Limit or RouteOrderType.StopLimit ? request.LimitPrice!.Value : 0m),
            ["StopLossPrice"] = Num(request.StopPrice ?? 0m),
            ["OrderType"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["Qty"] = request.Quantity,
            ["DisQty"] = "0",
            ["IsIntraday"] = false,
            ["iOrderValidity"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "3" : "0",
            ["AHPlaced"] = "N",
            ["RemoteOrderID"] = remoteId,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var book = await BookAsync(environment, ct).ConfigureAwait(false);
        var row = book.FirstOrDefault(o => Str(o, "BrokerOrderId") == order.OrderId);
        var exchangeId = Str(row, "ExchOrderID");
        if (exchangeId.Length == 0 || exchangeId == "0")
            throw new BrokerOrderRouteException("5paisa: the order has no exchange order id yet, and a cancel needs one — try again in a moment.", isRejection: true);
        var body = await CallAsync(environment, "V1/CancelOrderRequest", new JsonObject { ["ExchOrderID"] = exchangeId }, ct).ConfigureAwait(false);
        if ((int)Dec(body, "Status") != 0)
            throw RefusedInBody(Str(body, "Message"), body.GetRawText());
    }

    private async Task<JsonElement[]> BookAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var body = await CallAsync(environment, "V4/OrderBook", new JsonObject { ["ClientCode"] = ClientCode }, ct).ConfigureAwait(false);
        return body.TryGetProperty("OrderBookDetail", out var rows) && rows.ValueKind == JsonValueKind.Array ? [.. rows.EnumerateArray()] : [];
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exch, type, scrip) = Parts(symbol);
        var book = await BookAsync(environment, ct).ConfigureAwait(false);
        return [.. book
            .Where(o => Str(o, "Exch") == exch && Str(o, "ExchType") == type && Str(o, "ScripCode") == scrip)
            .Select(o => ReadOrder(o, symbol))];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);

    /// <summary><c>{"BrokerOrderId","ExchOrderID","OrderStatus","BuySell":"B|S","Qty","TradedQty","PendingQty","Rate","SLTriggerRate",
    /// "AtMarket","AveragePrice","RemoteOrderID","BrokerOrderTime":"/Date(ms+0530)/","Reason"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "TradedQty");
        var status = Str(o, "OrderStatus").Trim().ToLowerInvariant() switch
        {
            "fully executed" => RouteOrderStatus.Filled,
            "cancelled" or "ah cancelled" => RouteOrderStatus.Cancelled,
            "rejected by 5p" or "rejected by exch" or "rejected" => RouteOrderStatus.Rejected,
            "pending" or "xmitted" or "modified" or "ah placed" or "ah modified" or "partially executed" =>
                filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var atMarket = Str(o, "AtMarket") == "Y";
        var trigger = Positive(o, "SLTriggerRate");
        return new RouteOrder(
            Str(o, "BrokerOrderId"),
            EngineId(Str(o, "RemoteOrderID")),
            symbol,
            Str(o, "BuySell") == "S" ? OrderSide.Sell : OrderSide.Buy,
            (atMarket, trigger) switch
            {
                (true, null) => RouteOrderType.Market,
                (true, _) => RouteOrderType.Stop,
                (false, null) => RouteOrderType.Limit,
                _ => RouteOrderType.StopLimit,
            },
            RouteTimeInForce.Day,
            Dec(o, "Qty"),
            atMarket ? null : Positive(o, "Rate"),
            trigger,
            status,
            filled,
            Positive(o, "AveragePrice"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "Reason") is { Length: > 0 } reason ? reason : null,
            MicrosoftDate(Str(o, "BrokerOrderTime")) ?? Now.UtcDateTime);
    }

    /// <summary><c>/Date(1707279047230+0530)/</c> — the milliseconds are UTC; the offset only says where it was written.</summary>
    internal static DateTime? MicrosoftDate(string text)
    {
        var match = DateMs().Match(text);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
    }

    [GeneratedRegex(@"/Date\((-?\d+)")]
    private static partial Regex DateMs();

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var holdings = await CallAsync(environment, "V3/Holding", new JsonObject { ["ClientCode"] = ClientCode }, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, "V3/NetPositionNetWise", new JsonObject { ["ClientCode"] = ClientCode }, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>{"Data":[{"Exch","NseCode","BseCode","Quantity"}]}</c> plus the day's change in delivery positions
    /// <c>{"NetPositionDetail":[{"Exch","ExchType","ScripCode","OrderFor":"D","NetQty","BodQty"}]}</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (exch, type, scrip) = Parts(symbol);
        var codeField = exch == "B" ? "BseCode" : "NseCode";
        var held = holdings.TryGetProperty("Data", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Where(row => Str(row, codeField) == scrip).Sum(row => Dec(row, "Quantity"))
            : 0m;
        var today = positions.TryGetProperty("NetPositionDetail", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray()
                .Where(row => Str(row, "Exch") == exch && Str(row, "ExchType") == type && Str(row, "ScripCode") == scrip && Str(row, "OrderFor") == "D")
                .Sum(row => Dec(row, "NetQty") - Dec(row, "BodQty"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exch, type, scrip) = Parts(symbol);
        var body = await CallAsync(environment, "MarketSnapshot", new JsonObject
        {
            ["ClientCode"] = ClientCode,
            ["Data"] = new JsonArray(new JsonObject { ["Exchange"] = exch, ["ExchangeType"] = type, ["ScripCode"] = scrip, ["ScripData"] = "" }),
        }, ct).ConfigureAwait(false);
        var row = body.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : default;
        return Positive(row, "LastRate") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}
