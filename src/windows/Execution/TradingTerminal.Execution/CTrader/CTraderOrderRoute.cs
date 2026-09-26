using System.Collections.Concurrent;
using System.Globalization;
using Google.Protobuf;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.CTrader;

/// <summary>
/// cTrader Open API orders as an order route, so a cTrader account carries books, takes the manual ticket and
/// the strategies bound to it, and switches between demo and live like every other routed broker.
///
/// <para><b>Credentials</b> are the cTrader login row's — OAuth client id and secret, the access token and the
/// ctid account — read through <see cref="IBrokerCredentialSource"/> on every connection, so a token renewed in
/// the login window is used at once. <c>appsettings</c> is not involved; the older config-bound
/// <see cref="CTraderExecutionAdapter"/> card still appears only when configured.</para>
///
/// <para><b>Paper is the cTrader demo</b> (demo.ctraderapi.com). A ctid account is either a demo or a live
/// account — the account list says which — so a card asking for the other environment is refused with that
/// said, before any order.</para>
///
/// <para><b>Units.</b> Open API volumes are hundredths of a unit; a route quantity is units (volume / 100).
/// The engine's unit is the symbol's volume step (0.01 lot of EURUSD is 1,000 units), so a fill is always a
/// whole number of engine units. Order prices are the symbol's decimals; spot prices arrive as integers scaled
/// by 100,000.</para>
///
/// <para><b>Hedged accounts.</b> On a hedging account an opposite order opens a second position instead of
/// reducing the first. A market order that one open opposite position can absorb is therefore sent against
/// that position (<c>positionId</c>), which closes or reduces it; anything else is sent as a plain order, and the
/// net — which is what the engine books — is still right.</para>
///
/// <para>One connection per environment, request/response correlated by <c>clientMsgId</c>; unsolicited
/// events other than spots are ignored because the engine polls. A server error or an order error is a
/// refusal; a timeout or a dropped connection is an unknown outcome. Written 2026-09-25 from the Open API 2.0
/// reference and the existing adapter's wire handling; not yet run against a real account.</para>
/// </summary>
public sealed class CTraderOrderRoute : IBrokerOrderRoute, IAsyncDisposable, IDisposable
{
    /// <summary>Spot prices are integers in units of 10^-5.</summary>
    private const decimal SpotScale = 100_000m;

    private readonly IBrokerCredentialSource _credentials;
    private readonly Func<CTraderExecutionEndpoint, ICTraderExecutionTransport> _transports;
    private readonly TimeProvider _time;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Session? _session;
    private int _disposed;

    public CTraderOrderRoute(IBrokerCredentialSource credentials)
        : this(credentials, endpoint => new CTraderTlsExecutionTransport(endpoint), TimeProvider.System, TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>With an explicit transport factory, clock and request timeout — a scripted peer in tests.</summary>
    public CTraderOrderRoute(
        IBrokerCredentialSource credentials,
        Func<CTraderExecutionEndpoint, ICTraderExecutionTransport> transports,
        TimeProvider time,
        TimeSpan requestTimeout)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _time = time ?? TimeProvider.System;
        _requestTimeout = requestTimeout;
    }

    public BrokerKind Broker => BrokerKind.CTrader;

    public string DisplayName => "cTrader";

    public string RouteId => "ctrader";

    public string? PaperEnvironmentName => "DEMO";

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Session ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One authenticated connection to one environment and account.</summary>
    private sealed class Session : IAsyncDisposable
    {
        public required RouteEnvironment Environment { get; init; }

        /// <summary>Set once the account is authenticated; spots for any other account are ignored.</summary>
        public long AccountId { get; set; }

        public required string CredentialFingerprint { get; init; }

        public required ICTraderExecutionTransport Transport { get; init; }

        public ConcurrentDictionary<string, TaskCompletionSource<IMessage>> Pending { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<long, (decimal Bid, decimal Ask, DateTime AtUtc)> Spots { get; } = new();

        public ConcurrentDictionary<long, TaskCompletionSource<bool>> FirstSpot { get; } = new();

        public ConcurrentDictionary<string, ProtoOALightSymbol> SymbolsByName { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<long, ProtoOASymbol> SymbolsById { get; } = new();

        public ConcurrentDictionary<long, string> AssetNames { get; } = new();

        public ProtoOAAccountType AccountType { get; set; } = ProtoOAAccountType.Hedged;

        public volatile bool Broken;

        public bool IsUsable => !Broken && Transport.IsConnected;

        public async ValueTask DisposeAsync()
        {
            Broken = true;
            foreach (var pending in Pending.Values)
                pending.TrySetException(new IOException("The cTrader connection was closed."));
            try
            {
                await Transport.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Closing a dead socket is not a fault worth reporting.
            }
        }
    }

    private static string Fingerprint(BrokerCredential credential) =>
        $"{credential.Key.Trim()}|{credential.Account.Trim()}|{credential.Session.Length}:{credential.Session.GetHashCode(StringComparison.Ordinal)}";

    private async Task<Session> SessionAsync(RouteEnvironment environment, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var credential = _credentials.For(BrokerKind.CTrader);
        var fingerprint = Fingerprint(credential);
        var current = _session;
        if (current is { IsUsable: true } && current.Environment == environment && current.CredentialFingerprint == fingerprint)
            return current;

        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _session;
            if (current is { IsUsable: true } && current.Environment == environment && current.CredentialFingerprint == fingerprint)
                return current;
            if (current is not null)
            {
                _session = null;
                await current.DisposeAsync().ConfigureAwait(false);
            }

            _session = await OpenAsync(environment, credential, fingerprint, ct).ConfigureAwait(false);
            return _session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<Session> OpenAsync(RouteEnvironment environment, BrokerCredential credential, string fingerprint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential.Key) || string.IsNullOrWhiteSpace(credential.Secret) || string.IsNullOrWhiteSpace(credential.Session))
            throw new BrokerOrderRouteException(
                "cTrader: no OAuth client id, secret and access token are stored — enter them in the cTrader login row.", isRejection: true);

        var live = environment == RouteEnvironment.Live;
        var endpoint = new CTraderExecutionEndpoint(
            live ? ExecutionMode.Live : ExecutionMode.Paper,
            live ? CTraderExecutionOptions.LiveHost : CTraderExecutionOptions.DemoHost,
            CTraderExecutionOptions.OpenApiPort);
        var transport = _transports(endpoint);
        var session = new Session
        {
            Environment = environment,
            CredentialFingerprint = fingerprint,
            Transport = transport,
        };
        transport.MessageReceived += envelope => OnMessage(session, envelope);
        transport.Faulted += exception =>
        {
            session.Broken = true;
            foreach (var pending in session.Pending.Values)
                pending.TrySetException(new IOException("The cTrader connection dropped.", exception));
        };

        try
        {
            await transport.ConnectAsync(ct).ConfigureAwait(false);
            _ = await RequestAsync<ProtoOAApplicationAuthRes>(session,
                new ProtoOAApplicationAuthReq { ClientId = credential.Key.Trim(), ClientSecret = credential.Secret.Trim() }, ct).ConfigureAwait(false);

            var accounts = await RequestAsync<ProtoOAGetAccountListByAccessTokenRes>(session,
                new ProtoOAGetAccountListByAccessTokenReq { AccessToken = credential.Session.Trim() }, ct).ConfigureAwait(false);
            var accountId = PickAccount(accounts, credential.Account, live);
            if (accounts.HasPermissionScope && accounts.PermissionScope != ProtoOAClientPermissionScope.ScopeTrade)
                throw new BrokerOrderRouteException(
                    "cTrader: the access token was issued for viewing only; issue one with the trading scope.", isRejection: true);

            var auth = await RequestAsync<ProtoOAAccountAuthRes>(session,
                new ProtoOAAccountAuthReq { CtidTraderAccountId = accountId, AccessToken = credential.Session.Trim() }, ct).ConfigureAwait(false);
            if (auth.CtidTraderAccountId != accountId)
                throw new BrokerOrderRouteException("cTrader authenticated a different account than the one asked for.", isRejection: true);

            session.AccountId = accountId;
            return session;
        }
        catch
        {
            session.Broken = true;
            try
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The original failure is the one worth reporting.
            }

            throw;
        }
    }

    /// <summary>The stored ctid account if it is in the token's list and in the environment asked for; else, when
    /// none is stored, the only account of that environment.</summary>
    internal static long PickAccount(ProtoOAGetAccountListByAccessTokenRes accounts, string stored, bool live)
    {
        var environment = live ? "live" : "demo";
        if (long.TryParse(stored?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted) && wanted > 0)
        {
            var match = accounts.CtidTraderAccount.FirstOrDefault(a => (long)a.CtidTraderAccountId == wanted)
                ?? throw new BrokerOrderRouteException(
                    $"cTrader: account {wanted} is not among the accounts this access token reaches.", isRejection: true);
            if (match.HasIsLive && match.IsLive != live)
                throw new BrokerOrderRouteException(
                    $"cTrader: account {wanted} is a {(match.IsLive ? "live" : "demo")} account, and this card trades {environment}. "
                    + $"Pick a {environment} account in the cTrader login row.", isRejection: true);
            return wanted;
        }

        var candidates = accounts.CtidTraderAccount.Where(a => a.HasIsLive && a.IsLive == live).ToArray();
        return candidates.Length == 1
            ? (long)candidates[0].CtidTraderAccountId
            : throw new BrokerOrderRouteException(
                $"cTrader: {(candidates.Length == 0 ? "no" : "more than one")} {environment} account is reachable with this token — pick one in the cTrader login row.",
                isRejection: true);
    }

    private void OnMessage(Session session, ProtoMessage envelope)
    {
        IMessage? message;
        try
        {
            message = CTraderOpenApiProtocol.Decode(envelope);
        }
        catch
        {
            return;
        }

        if (message is null)
            return;
        if (envelope.HasClientMsgId && session.Pending.TryRemove(envelope.ClientMsgId, out var waiting))
        {
            waiting.TrySetResult(message);
            return;
        }

        switch (message)
        {
            case ProtoOASpotEvent spot when spot.CtidTraderAccountId == session.AccountId:
                var previous = session.Spots.TryGetValue(spot.SymbolId, out var known) ? known : (0m, 0m, DateTime.MinValue);
                var bid = spot.HasBid ? spot.Bid / SpotScale : previous.Item1;
                var ask = spot.HasAsk ? spot.Ask / SpotScale : previous.Item2;
                session.Spots[spot.SymbolId] = (bid, ask, UtcNow);
                if (bid > 0 && ask > 0 && session.FirstSpot.TryGetValue(spot.SymbolId, out var first))
                    first.TrySetResult(true);
                break;
            case ProtoOAAccountDisconnectEvent or ProtoOAAccountsTokenInvalidatedEvent:
                session.Broken = true;
                break;
        }
    }

    /// <summary>One correlated request. A server error or an order error is the broker's refusal; a timeout or a
    /// dropped connection is left as itself, which the engine treats as an unknown outcome.</summary>
    private async Task<T> RequestAsync<T>(Session session, IMessage request, CancellationToken ct) where T : class, IMessage
    {
        var id = $"dax-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<IMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Pending[id] = completion;
        try
        {
            await session.Transport.SendAsync(CTraderOpenApiProtocol.Encode(request, id), ct).ConfigureAwait(false);
            var answer = await completion.Task.WaitAsync(_requestTimeout, _time, ct).ConfigureAwait(false);
            return answer switch
            {
                T typed => typed,
                ProtoOAErrorRes error => throw new BrokerOrderRouteException(
                    $"cTrader: {Words(error.Description, error.ErrorCode)}", isRejection: true),
                ProtoOAOrderErrorEvent error => throw new BrokerOrderRouteException(
                    $"cTrader: {Words(error.Description, error.ErrorCode)}", isRejection: true),
                _ => throw new InvalidDataException($"cTrader answered {answer.GetType().Name} where {typeof(T).Name} was expected."),
            };
        }
        finally
        {
            session.Pending.TryRemove(id, out _);
        }
    }

    private static string Words(string description, string code) =>
        string.IsNullOrWhiteSpace(description) ? code : string.IsNullOrWhiteSpace(code) ? description : $"{description} ({code})";

    // ── Account ──────────────────────────────────────────────────────────────────────────────────

    public async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var trader = await RequestAsync<ProtoOATraderRes>(session, new ProtoOATraderReq { CtidTraderAccountId = session.AccountId }, ct)
            .ConfigureAwait(false);
        if (trader.Trader is null)
            throw new InvalidDataException("cTrader returned no trader for the account.");
        if (trader.Trader.HasAccountType)
            session.AccountType = trader.Trader.AccountType;
        var money = Money(trader.Trader.Balance, trader.Trader.HasMoneyDigits ? trader.Trader.MoneyDigits : 2u);
        var currency = await AssetNameAsync(session, trader.Trader.DepositAssetId, ct).ConfigureAwait(false);
        return new RouteAccount(session.AccountId.ToString(CultureInfo.InvariantCulture), currency, money, money);
    }

    public Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct) => ConnectAsync(environment, ct);

    private static decimal Money(long value, uint digits)
    {
        var scale = 1m;
        for (var i = 0; i < digits; i++)
            scale *= 10m;
        return value / scale;
    }

    private async Task<string> AssetNameAsync(Session session, long assetId, CancellationToken ct)
    {
        if (session.AssetNames.TryGetValue(assetId, out var known))
            return known;
        var assets = await RequestAsync<ProtoOAAssetListRes>(session, new ProtoOAAssetListReq { CtidTraderAccountId = session.AccountId }, ct)
            .ConfigureAwait(false);
        foreach (var asset in assets.Asset)
            session.AssetNames[asset.AssetId] = asset.Name;
        return session.AssetNames.TryGetValue(assetId, out known) ? known : "USD";
    }

    // ── Instruments ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The light symbol for a name (<c>EURUSD</c>, <c>EUR/USD</c> and <c>eurusd</c> alike) or a numeric id.</summary>
    private async Task<ProtoOALightSymbol> LightSymbolAsync(Session session, string symbol, CancellationToken ct)
    {
        var name = Normalise(symbol);
        if (session.SymbolsByName.TryGetValue(name, out var known))
            return known;
        var list = await RequestAsync<ProtoOASymbolsListRes>(session,
            new ProtoOASymbolsListReq { CtidTraderAccountId = session.AccountId, IncludeArchivedSymbols = false }, ct).ConfigureAwait(false);
        foreach (var light in list.Symbol)
        {
            session.SymbolsByName[Normalise(light.SymbolName)] = light;
            session.SymbolsByName[light.SymbolId.ToString(CultureInfo.InvariantCulture)] = light;
        }

        return session.SymbolsByName.TryGetValue(name, out known)
            ? known
            : throw new BrokerOrderRouteException($"cTrader: this account has no symbol {symbol.Trim()}.", isRejection: true);
    }

    internal static string Normalise(string symbol) => symbol.Trim().Replace("/", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private async Task<(ProtoOALightSymbol Light, ProtoOASymbol Full)> SymbolAsync(Session session, string symbol, CancellationToken ct)
    {
        var light = await LightSymbolAsync(session, symbol, ct).ConfigureAwait(false);
        if (session.SymbolsById.TryGetValue(light.SymbolId, out var full))
            return (light, full);
        var request = new ProtoOASymbolByIdReq { CtidTraderAccountId = session.AccountId };
        request.SymbolId.Add(light.SymbolId);
        var answer = await RequestAsync<ProtoOASymbolByIdRes>(session, request, ct).ConfigureAwait(false);
        full = answer.Symbol.FirstOrDefault(s => s.SymbolId == light.SymbolId)
            ?? throw new InvalidDataException($"cTrader returned no details for {symbol.Trim()}.");
        session.SymbolsById[light.SymbolId] = full;
        return (light, full);
    }

    public async Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (light, full) = await SymbolAsync(session, symbol, ct).ConfigureAwait(false);
        var quote = await AssetNameAsync(session, light.QuoteAssetId, ct).ConfigureAwait(false);
        return ReadInstrument(symbol.Trim(), full, quote);
    }

    /// <summary>
    /// <c>{"digits","minVolume","maxVolume","stepVolume","tradingMode"}</c>, volumes in hundredths of a unit. The
    /// engine unit is the volume step; a point of price is worth one unit per unit held, in the quote currency.
    /// </summary>
    internal static RouteInstrument ReadInstrument(string symbol, ProtoOASymbol full, string quoteCurrency)
    {
        if (full.HasTradingMode && full.TradingMode != ProtoOATradingMode.Enabled)
            throw new BrokerOrderRouteException($"cTrader: {symbol} is not open for trading ({full.TradingMode}).", isRejection: true);
        if (!full.HasStepVolume || full.StepVolume <= 0 || !full.HasDigits)
            throw new InvalidDataException($"cTrader returned no volume step or price digits for {symbol}.");
        var unit = full.StepVolume / 100m;
        var tick = 1m;
        for (var i = 0; i < full.Digits; i++)
            tick /= 10m;
        var minimum = full.HasMinVolume && full.MinVolume > 0 ? (long)Math.Ceiling(full.MinVolume / (decimal)full.StepVolume) : 1L;
        var maximum = full.HasMaxVolume && full.MaxVolume > 0 ? Math.Max(minimum, full.MaxVolume / full.StepVolume) : 10_000_000L;
        return new RouteInstrument(
            symbol, unit, unit, tick, Math.Max(1, minimum), maximum,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: true, string.IsNullOrWhiteSpace(quoteCurrency) ? "USD" : quoteCurrency);
    }

    // ── Orders ───────────────────────────────────────────────────────────────────────────────────

    public async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (light, full) = await SymbolAsync(session, request.Symbol, ct).ConfigureAwait(false);
        var order = BuildOrder(session.AccountId, light.SymbolId, full, request);

        // On a hedging account, a market order one open opposite position can absorb closes that position
        // rather than opening a second one beside it.
        if (request.Type == RouteOrderType.Market && session.AccountType == ProtoOAAccountType.Hedged)
        {
            var reconcile = await ReconcileAsync(session, ct).ConfigureAwait(false);
            if (ClosablePosition(reconcile, light.SymbolId, order.TradeSide, order.Volume) is { } positionId)
                order.PositionId = positionId;
        }

        var execution = await RequestAsync<ProtoOAExecutionEvent>(session, order, ct).ConfigureAwait(false);
        if (execution.Order is { } placed)
            return ReadOrder(placed, request.Symbol, full.Digits) with { ClientOrderId = request.ClientOrderId };
        throw new InvalidDataException("cTrader acknowledged the order without describing it.");
    }

    /// <summary>The order as Open API spells it: volume in hundredths, prices on the symbol's decimals.</summary>
    internal static ProtoOANewOrderReq BuildOrder(long accountId, long symbolId, ProtoOASymbol full, RouteOrderRequest request)
    {
        var volume = request.Quantity * 100m;
        if (volume != decimal.Truncate(volume) || volume <= 0)
            throw new BrokerOrderRouteException($"cTrader: {request.Quantity} is not a whole number of hundredths of a unit.", isRejection: true);
        var order = new ProtoOANewOrderReq
        {
            CtidTraderAccountId = accountId,
            SymbolId = symbolId,
            OrderType = request.Type switch
            {
                RouteOrderType.Limit => ProtoOAOrderType.Limit,
                RouteOrderType.Stop => ProtoOAOrderType.Stop,
                RouteOrderType.StopLimit => ProtoOAOrderType.StopLimit,
                _ => ProtoOAOrderType.Market,
            },
            TradeSide = request.Side == OrderSide.Buy ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell,
            Volume = (long)volume,
            ClientOrderId = request.ClientOrderId.Length <= 50 ? request.ClientOrderId : request.ClientOrderId[..50],
            Label = "DaxAlgo",
        };
        if (request.Type != RouteOrderType.Market)
        {
            order.TimeInForce = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => ProtoOATimeInForce.ImmediateOrCancel,
                RouteTimeInForce.FillOrKill => ProtoOATimeInForce.FillOrKill,
                _ => ProtoOATimeInForce.GoodTillCancel,
            };
        }

        if (request.Type is RouteOrderType.Limit or RouteOrderType.StopLimit)
            order.LimitPrice = (double)request.LimitPrice!.Value;
        if (request.Type is RouteOrderType.Stop or RouteOrderType.StopLimit)
            order.StopPrice = (double)request.StopPrice!.Value;
        if (request.Type == RouteOrderType.StopLimit)
        {
            // Open API expresses a stop-limit's limit as slippage in points from the stop.
            var slippage = Math.Abs(request.LimitPrice!.Value - request.StopPrice!.Value) * Pow10(full.Digits);
            order.SlippageInPoints = (int)Math.Round(slippage, MidpointRounding.AwayFromZero);
        }

        return order;
    }

    private static decimal Pow10(int digits)
    {
        var scale = 1m;
        for (var i = 0; i < digits; i++)
            scale *= 10m;
        return scale;
    }

    /// <summary>The smallest open opposite position in the symbol whose volume covers the order, if any.</summary>
    internal static long? ClosablePosition(ProtoOAReconcileRes reconcile, long symbolId, ProtoOATradeSide side, long volume) =>
        reconcile.Position
            .Where(p => p.TradeData is { } t && t.SymbolId == symbolId && t.TradeSide != side && t.Volume >= volume &&
                        (!p.HasPositionStatus || p.PositionStatus == ProtoOAPositionStatus.PositionStatusOpen))
            .OrderBy(p => p.TradeData.Volume)
            .Select(p => (long?)p.PositionId)
            .FirstOrDefault();

    public async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        _ = await RequestAsync<ProtoOAExecutionEvent>(session,
            new ProtoOACancelOrderReq { CtidTraderAccountId = session.AccountId, OrderId = long.Parse(order.OrderId, CultureInfo.InvariantCulture) }, ct)
            .ConfigureAwait(false);
    }

    public async Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (_, full) = await SymbolAsync(session, order.Symbol, ct).ConfigureAwait(false);
        var amend = new ProtoOAAmendOrderReq
        {
            CtidTraderAccountId = session.AccountId,
            OrderId = long.Parse(order.OrderId, CultureInfo.InvariantCulture),
            Volume = (long)(quantity * 100m),
        };
        if (limitPrice is { } limit) amend.LimitPrice = (double)limit;
        if (stopPrice is { } stop) amend.StopPrice = (double)stop;
        var execution = await RequestAsync<ProtoOAExecutionEvent>(session, amend, ct).ConfigureAwait(false);
        return execution.Order is { } amended
            ? ReadOrder(amended, order.Symbol, full.Digits) with { ClientOrderId = order.ClientOrderId }
            : order with { Quantity = quantity, LimitPrice = limitPrice, StopPrice = stopPrice, UpdatedAtUtc = UtcNow };
    }

    private async Task<ProtoOAReconcileRes> ReconcileAsync(Session session, CancellationToken ct) =>
        await RequestAsync<ProtoOAReconcileRes>(session, new ProtoOAReconcileReq { CtidTraderAccountId = session.AccountId }, ct)
            .ConfigureAwait(false);

    /// <summary>The account's pending orders in the symbol. A filled or cancelled order has left this list; the
    /// engine then asks for it by id.</summary>
    public async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (light, full) = await SymbolAsync(session, symbol, ct).ConfigureAwait(false);
        var reconcile = await ReconcileAsync(session, ct).ConfigureAwait(false);
        return [.. reconcile.Order
            .Where(o => o.TradeData is { } t && t.SymbolId == light.SymbolId)
            .Select(o => ReadOrder(o, symbol, full.Digits))];
    }

    /// <summary>One order from the last three days' order history, whatever its state.</summary>
    public async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (!long.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (_, full) = await SymbolAsync(session, symbol, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var history = await RequestAsync<ProtoOAOrderListRes>(session, new ProtoOAOrderListReq
        {
            CtidTraderAccountId = session.AccountId,
            FromTimestamp = now.AddDays(-3).ToUnixTimeMilliseconds(),
            ToTimestamp = now.AddMinutes(1).ToUnixTimeMilliseconds(),
        }, ct).ConfigureAwait(false);
        return history.Order.FirstOrDefault(o => o.OrderId == id) is { } order ? ReadOrder(order, symbol, full.Digits) : null;
    }

    /// <summary>
    /// <c>{"orderId","clientOrderId","orderType","orderStatus","timeInForce","tradeData":{"volume","tradeSide"},"executedVolume",
    /// "executionPrice","limitPrice","stopPrice","utcLastUpdateTimestamp"}</c>. <c>executionPrice</c> is the average over what
    /// has filled; volumes are hundredths.
    /// </summary>
    internal static RouteOrder ReadOrder(ProtoOAOrder order, string symbol, int digits)
    {
        var filled = order.HasExecutedVolume ? order.ExecutedVolume / 100m : 0m;
        var status = order.OrderStatus switch
        {
            ProtoOAOrderStatus.OrderStatusFilled => RouteOrderStatus.Filled,
            ProtoOAOrderStatus.OrderStatusCancelled => RouteOrderStatus.Cancelled,
            ProtoOAOrderStatus.OrderStatusExpired => RouteOrderStatus.Expired,
            ProtoOAOrderStatus.OrderStatusRejected => RouteOrderStatus.Rejected,
            ProtoOAOrderStatus.OrderStatusAccepted => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var trade = order.TradeData;
        return new RouteOrder(
            order.OrderId.ToString(CultureInfo.InvariantCulture),
            order.HasClientOrderId ? order.ClientOrderId : string.Empty,
            symbol,
            trade is { TradeSide: ProtoOATradeSide.Sell } ? OrderSide.Sell : OrderSide.Buy,
            order.OrderType switch
            {
                ProtoOAOrderType.Limit => RouteOrderType.Limit,
                ProtoOAOrderType.Stop => RouteOrderType.Stop,
                ProtoOAOrderType.StopLimit => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            order.TimeInForce switch
            {
                ProtoOATimeInForce.ImmediateOrCancel => RouteTimeInForce.ImmediateOrCancel,
                ProtoOATimeInForce.FillOrKill => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.GoodTillCancelled,
            },
            trade is { HasVolume: true } ? trade.Volume / 100m : 0m,
            order.HasLimitPrice ? decimal.Round((decimal)order.LimitPrice, digits) : null,
            order.HasStopPrice ? decimal.Round((decimal)order.StopPrice, digits) : null,
            status,
            filled,
            filled > 0 && order.HasExecutionPrice ? decimal.Round((decimal)order.ExecutionPrice, Math.Min(12, digits + 5)) : null,
            0m,
            string.Empty,
            null,
            order.HasUtcLastUpdateTimestamp
                ? DateTimeOffset.FromUnixTimeMilliseconds(order.UtcLastUpdateTimestamp).UtcDateTime
                : DateTime.UtcNow);
    }

    public async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (light, _) = await SymbolAsync(session, symbol, ct).ConfigureAwait(false);
        var reconcile = await ReconcileAsync(session, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, NetPosition(reconcile, light.SymbolId));
    }

    /// <summary>The net of every open position in the symbol: buys less sells, in units.</summary>
    internal static decimal NetPosition(ProtoOAReconcileRes reconcile, long symbolId) =>
        reconcile.Position
            .Where(p => p.TradeData is { } t && t.SymbolId == symbolId &&
                        (!p.HasPositionStatus || p.PositionStatus == ProtoOAPositionStatus.PositionStatusOpen))
            .Sum(p => (p.TradeData.TradeSide == ProtoOATradeSide.Sell ? -1m : 1m) * p.TradeData.Volume / 100m);

    // ── Prices ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The mid of the latest spot. The first request subscribes the symbol and waits briefly for its
    /// first quote; later ones read what the subscription keeps current.</summary>
    public async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var (light, _) = await SymbolAsync(session, symbol, ct).ConfigureAwait(false);
        var first = session.FirstSpot.GetOrAdd(light.SymbolId, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!session.Spots.ContainsKey(light.SymbolId) && !first.Task.IsCompleted)
        {
            var subscribe = new ProtoOASubscribeSpotsReq { CtidTraderAccountId = session.AccountId };
            subscribe.SymbolId.Add(light.SymbolId);
            try
            {
                _ = await RequestAsync<ProtoOASubscribeSpotsRes>(session, subscribe, ct).ConfigureAwait(false);
            }
            catch (BrokerOrderRouteException exception) when (exception.Message.Contains("ALREADY_SUBSCRIBED", StringComparison.OrdinalIgnoreCase))
            {
                // A second card on the same connection already asked for it.
            }

            try
            {
                await first.Task.WaitAsync(TimeSpan.FromSeconds(3), _time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        return session.Spots.TryGetValue(light.SymbolId, out var spot) && spot.Bid > 0 && spot.Ask > 0
            ? new RoutePrice((spot.Bid + spot.Ask) / 2m, spot.AtUtc)
            : null;
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
        _sessionGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
