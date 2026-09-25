using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Binance;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Mexc;

/// <summary>
/// MEXC spot orders: Binance's API shape at api.mexc.com, with its own differences — immediate-or-cancel
/// and fill-or-kill are order <i>types</i> rather than a time-in-force, instrument precision is given as a
/// step and a number of decimals rather than filters, stop orders are not offered, and the account answer
/// names no account (the key stands in). MEXC has no test environment. Written 2026-09-25 from MEXC's spot
/// v3 reference; not yet run against a real account.
/// </summary>
internal sealed class MexcOrderRoute : BinanceShapedOrderRoute
{
    public MexcOrderRoute(IBrokerCredentialSource credentials, ILogger<MexcOrderRoute> logger)
        : base(credentials, logger) { }

    internal MexcOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Mexc;
    public override string DisplayName => "MEXC";
    public override string RouteId => "mexc";
    public override string? PaperEnvironmentName => null;
    protected override string KeyHeader => "X-MEXC-APIKEY";

    protected override string Host(RouteEnvironment environment) => "https://api.mexc.com";

    protected override string Signature(string query, string secret) => CryptoAuth.MexcSignature(query, secret);

    /// <summary><c>{"symbols":[{"symbol","baseAsset","quoteAsset","baseSizePrecision":"0.000001",
    /// "quotePrecision":2,"orderTypes":["LIMIT","MARKET"],"maxQuoteAmount"}]}</c>.</summary>
    internal override RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array || symbols.GetArrayLength() == 0)
            return null;
        var s = symbols[0];
        var step = Dec(s, "baseSizePrecision") is > 0 and var size ? size : Step((int)Dec(s, "baseAssetPrecision"));
        var tick = Step((int)Dec(s, "quotePrecision"));
        var types = RouteOrderTypes.None;
        if (s.TryGetProperty("orderTypes", out var orderTypes))
            foreach (var type in orderTypes.EnumerateArray())
                types |= type.GetString() switch { "MARKET" => RouteOrderTypes.Market, "LIMIT" => RouteOrderTypes.Limit, _ => RouteOrderTypes.None };
        return new RouteInstrument(
            Str(s, "symbol"), step, step, tick, 1, long.MaxValue / 2,
            types == RouteOrderTypes.None ? RouteOrderTypes.Limit : types,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "quoteAsset"))
        {
            BaseAsset = Str(s, "baseAsset"),
        };
    }

    internal override string OrderQuery(RouteOrderRequest request)
    {
        var client = Token(request.ClientOrderId, id => id.Length <= 32 ? id : Hex(id, 32));
        var query = $"symbol={Uri.EscapeDataString(request.Symbol)}&side={(request.Side == OrderSide.Buy ? "BUY" : "SELL")}"
                    + $"&quantity={Num(request.Quantity)}&newClientOrderId={Uri.EscapeDataString(client)}";
        if (request.Type == RouteOrderType.Market)
            return query + "&type=MARKET";
        var type = request.TimeInForce switch
        {
            RouteTimeInForce.ImmediateOrCancel => "IMMEDIATE_OR_CANCEL",
            RouteTimeInForce.FillOrKill => "FILL_OR_KILL",
            _ => "LIMIT",
        };
        return query + $"&type={type}&price={Num(request.LimitPrice!.Value)}";
    }
}
