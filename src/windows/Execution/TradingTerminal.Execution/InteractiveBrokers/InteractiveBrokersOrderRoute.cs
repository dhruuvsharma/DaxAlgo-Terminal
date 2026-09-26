using System.Collections.Concurrent;
using System.Globalization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Execution.Routing;

namespace TradingTerminal.Execution.InteractiveBrokers;

/// <summary>
/// Interactive Brokers orders as an order route, over the existing TWS socket transport, so an IB account carries
/// books, takes the manual ticket and strategies, and switches between paper and live like every routed broker.
///
/// <para><b>Where TWS listens</b> is the IB login row's: host and port, and the API client id (this route
/// connects as that id + 20, so it never collides with the market-data connection). TWS and IB Gateway run paper
/// and live as separate sessions on separate ports — 7497/7496 for TWS, 4002/4001 for the Gateway — so PAPER
/// connects to the paper port of the row's family and LIVE to the live one, and a paper card that reaches an
/// account that is not a paper account (<c>DU…</c>) is refused, whichever port it came through.</para>
///
/// <para><b>State</b> comes from the transport's reconciliation snapshot — open and completed orders, positions
/// and the account summary — and fill prices from its execution reports, kept per order for the session. An
/// order filled before this session connected has no average here; the engine then reports it as an unknown
/// outcome rather than guessing. TWS answers an order it refuses asynchronously; the refusal is kept and the
/// order reads as rejected. There is no price call on this transport, so the book's reference price comes from
/// the terminal's own market data for the instrument.</para>
///
/// <para>Symbols: a US stock by its ticker (<c>AAPL</c>, routed SMART in USD) or a currency pair written
/// <c>EUR.USD</c> (IDEALPRO). Needs the TWS API DLL at build time (<c>HAS_IBAPI</c>); without it every call is
/// refused with that said. Written 2026-09-25; not yet run against a real TWS.</para>
/// </summary>
public sealed class InteractiveBrokersOrderRoute : IBrokerOrderRoute, IAsyncDisposable, IDisposable
{
    private const int ClientIdOffset = 20;

    private readonly IBrokerCredentialSource _credentials;
    private readonly Func<InteractiveBrokersExecutionEndpoint, TimeSpan, IInteractiveBrokersExecutionTransport> _transports;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Session? _session;
    private int _disposed;

    public InteractiveBrokersOrderRoute(IBrokerCredentialSource credentials)
        : this(credentials, (endpoint, timeout) => InteractiveBrokersExecutionTransportFactory.CreateDefault(endpoint, timeout), TimeProvider.System)
    {
    }

    /// <summary>With an explicit transport factory — a scripted TWS in tests.</summary>
    public InteractiveBrokersOrderRoute(
        IBrokerCredentialSource credentials,
        Func<InteractiveBrokersExecutionEndpoint, TimeSpan, IInteractiveBrokersExecutionTransport> transports,
        TimeProvider time)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transports = transports ?? throw new ArgumentNullException(nameof(transports));
        _time = time ?? TimeProvider.System;
    }

    public BrokerKind Broker => BrokerKind.InteractiveBrokers;

    public string DisplayName => "Interactive Brokers";

    /// <summary><c>ib</c>, not <c>interactive-brokers</c>: the config-bound IB card already owns
    /// <c>interactive-brokers-paper</c> and <c>-live</c>, and a card id must be unique.</summary>
    public string RouteId => "ib";

    public string? PaperEnvironmentName => "PAPER";

    private sealed class Session : IAsyncDisposable
    {
        public required RouteEnvironment Environment { get; init; }

        public required string Fingerprint { get; init; }

        public required IInteractiveBrokersExecutionTransport Transport { get; init; }

        public required string AccountId { get; init; }

        /// <summary>The latest execution report per order: cumulative quantity and average price.</summary>
        public ConcurrentDictionary<int, (decimal Quantity, decimal Average)> Fills { get; } = new();

        /// <summary>What TWS said when it refused an order.</summary>
        public ConcurrentDictionary<int, string> Refusals { get; } = new();

        public volatile bool Broken;

        public bool IsUsable => !Broken && Transport.IsConnected;

        public async ValueTask DisposeAsync()
        {
            Broken = true;
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

    /// <summary>The row's host (Key) and <c>port|clientId|accountType</c> (Extra); TWS paper on this machine when
    /// nothing is stored.</summary>
    internal static (string Host, int Port, int ClientId) Settings(BrokerCredential credential)
    {
        var host = string.IsNullOrWhiteSpace(credential.Key) ? "127.0.0.1" : credential.Key.Trim();
        var parts = credential.Extra.Split('|');
        var port = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 7497;
        var clientId = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 1;
        return (host, port, clientId);
    }

    /// <summary>The paper or live port of the same family (TWS or Gateway) as the row's port.</summary>
    public static int PortFor(RouteEnvironment environment, int rowPort)
    {
        var gateway = rowPort is InteractiveBrokersExecutionOptions.GatewayPaperPort or InteractiveBrokersExecutionOptions.GatewayLivePort;
        return environment == RouteEnvironment.Live
            ? gateway ? InteractiveBrokersExecutionOptions.GatewayLivePort : InteractiveBrokersExecutionOptions.TwsLivePort
            : gateway ? InteractiveBrokersExecutionOptions.GatewayPaperPort : InteractiveBrokersExecutionOptions.TwsPaperPort;
    }

    /// <summary>IB paper accounts are the <c>D…</c> ones (<c>DU1234567</c>); live accounts are not.</summary>
    internal static bool IsPaperAccount(string accountId) => accountId.Trim().StartsWith('D');

    private async Task<Session> SessionAsync(RouteEnvironment environment, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var (host, rowPort, clientId) = Settings(_credentials.For(BrokerKind.InteractiveBrokers));
        var port = PortFor(environment, rowPort);
        var fingerprint = $"{host}:{port}:{clientId}";
        var current = _session;
        if (current is { IsUsable: true } && current.Environment == environment && current.Fingerprint == fingerprint)
            return current;

        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _session;
            if (current is { IsUsable: true } && current.Environment == environment && current.Fingerprint == fingerprint)
                return current;
            if (current is not null)
            {
                _session = null;
                await current.DisposeAsync().ConfigureAwait(false);
            }

            var endpoint = new InteractiveBrokersExecutionEndpoint(
                environment == RouteEnvironment.Live ? ExecutionMode.Live : ExecutionMode.Paper, host, port);
            IInteractiveBrokersExecutionTransport transport;
            try
            {
                transport = _transports(endpoint, TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException exception)
            {
                throw new BrokerOrderRouteException($"Interactive Brokers: {exception.Message}", isRejection: true, exception);
            }

            InteractiveBrokersSessionSnapshot snapshot;
            try
            {
                snapshot = await transport.ConnectAsync(clientId + ClientIdOffset, string.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw new BrokerOrderRouteException(
                    $"Interactive Brokers: TWS or IB Gateway did not accept a connection at {host}:{port} "
                    + $"({(environment == RouteEnvironment.Live ? "live" : "paper")}) — {exception.Message}", isRejection: true, exception);
            }

            if (IsPaperAccount(snapshot.AccountId) != (environment == RouteEnvironment.Paper))
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw new BrokerOrderRouteException(
                    $"Interactive Brokers: the session at {host}:{port} is account {snapshot.AccountId}, a "
                    + $"{(IsPaperAccount(snapshot.AccountId) ? "paper" : "live")} account, and this card trades "
                    + $"{(environment == RouteEnvironment.Live ? "live" : "paper")}.", isRejection: true);
            }

            var session = new Session
            {
                Environment = environment,
                Fingerprint = fingerprint,
                Transport = transport,
                AccountId = snapshot.AccountId,
            };
            transport.ExecutionReceived += execution =>
            {
                var quantity = Dec(execution.CumulativeQuantity);
                if (quantity > 0 && Dec(execution.AveragePrice) is > 0 and var average &&
                    (!session.Fills.TryGetValue(execution.OrderId, out var known) || quantity >= known.Quantity))
                    session.Fills[execution.OrderId] = (quantity, average);
            };
            transport.OrderError += error =>
            {
                if (error.OrderId is { } id && id > 0)
                    session.Refusals[id] = $"{error.Message} ({error.ErrorCode})";
            };
            transport.Faulted += _ => session.Broken = true;
            _session = session;
            return session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ── Values ───────────────────────────────────────────────────────────────────────────────────

    private static decimal Scale(long coefficient, byte scale)
    {
        var value = (decimal)coefficient;
        for (var i = 0; i < scale; i++)
            value /= 10m;
        return value;
    }

    private static decimal Dec(ScaledQuantity quantity) => Scale(quantity.Coefficient, quantity.Scale);

    private static decimal Dec(ScaledPrice price) => Scale(price.Coefficient, price.Scale);

    private static decimal Dec(ScaledMoney money) => Scale(money.Coefficient, money.Scale);

    /// <summary><c>AAPL</c> is a SMART-routed US stock; <c>EUR.USD</c> an IDEALPRO currency pair.</summary>
    public static InteractiveBrokersContract Contract(string symbol)
    {
        var trimmed = symbol.Trim().ToUpperInvariant();
        var dot = trimmed.IndexOf('.');
        return dot == 3 && trimmed.Length == 7 && trimmed.All(c => char.IsAsciiLetter(c) || c == '.')
            ? new InteractiveBrokersContract(0, trimmed[..3], "CASH", "IDEALPRO", string.Empty, trimmed[4..])
            : new InteractiveBrokersContract(0, trimmed, "STK", "SMART", string.Empty, "USD");
    }

    private static bool Same(InteractiveBrokersContract contract, string symbol)
    {
        var wanted = Contract(symbol);
        return string.Equals(contract.Symbol, wanted.Symbol, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrEmpty(contract.SecurityType) || string.Equals(contract.SecurityType, wanted.SecurityType, StringComparison.OrdinalIgnoreCase)) &&
               (wanted.SecurityType != "CASH" || string.Equals(contract.Currency, wanted.Currency, StringComparison.OrdinalIgnoreCase));
    }

    // ── Account ──────────────────────────────────────────────────────────────────────────────────

    public async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var snapshot = await session.Transport.GetReconciliationSnapshotAsync(session.AccountId, ct).ConfigureAwait(false);
        var cash = snapshot.Cash.FirstOrDefault(item => item.Currency is "BASE" or "USD") ?? snapshot.Cash.FirstOrDefault();
        return new RouteAccount(session.AccountId, cash?.Currency is { Length: > 0 } c && c != "BASE" ? c : "USD",
            cash is null ? 0m : Dec(cash.TotalCash), cash is null ? 0m : Dec(cash.AvailableFunds));
    }

    public Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct) => ConnectAsync(environment, ct);

    public async Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var contract = Contract(symbol);
        InteractiveBrokersNativeCapabilities capabilities;
        try
        {
            capabilities = await session.Transport.DiscoverCapabilitiesAsync(contract, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new BrokerOrderRouteException($"Interactive Brokers: no contract details for {symbol.Trim()} — {exception.Message}", isRejection: true, exception);
        }

        return ReadInstrument(symbol.Trim(), contract, capabilities);
    }

    internal static RouteInstrument ReadInstrument(string symbol, InteractiveBrokersContract contract, InteractiveBrokersNativeCapabilities capabilities)
    {
        var unit = Dec(capabilities.QuantityIncrement) is > 0 and var step ? step : 1m;
        var tick = Dec(capabilities.MinimumPriceIncrement) is > 0 and var t ? t : 0.01m;
        var minimum = Dec(capabilities.MinimumOrderQuantity) is > 0 and var m ? (long)Math.Ceiling(m / unit) : 1L;
        var types = RouteOrderTypes.None;
        foreach (var type in capabilities.OrderTypes)
            types |= type switch
            {
                InteractiveBrokersNativeOrderType.Market => RouteOrderTypes.Market,
                InteractiveBrokersNativeOrderType.Limit => RouteOrderTypes.Limit,
                InteractiveBrokersNativeOrderType.Stop => RouteOrderTypes.Stop,
                InteractiveBrokersNativeOrderType.StopLimit => RouteOrderTypes.StopLimit,
                _ => RouteOrderTypes.None,
            };
        var times = RouteTimesInForce.None;
        foreach (var time in capabilities.TimeInForce)
            times |= time switch
            {
                InteractiveBrokersNativeTimeInForce.Day => RouteTimesInForce.Day,
                InteractiveBrokersNativeTimeInForce.GoodTillCancelled => RouteTimesInForce.GoodTillCancelled,
                InteractiveBrokersNativeTimeInForce.ImmediateOrCancel => RouteTimesInForce.ImmediateOrCancel,
                InteractiveBrokersNativeTimeInForce.FillOrKill => RouteTimesInForce.FillOrKill,
                _ => RouteTimesInForce.None,
            };
        return new RouteInstrument(
            symbol, unit, unit, tick, Math.Max(1, minimum), 10_000_000,
            types == RouteOrderTypes.None ? RouteOrderTypes.Market | RouteOrderTypes.Limit : types,
            times == RouteTimesInForce.None ? RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled : times,
            SupportsReplace: true, contract.Currency);
    }

    // ── Orders ───────────────────────────────────────────────────────────────────────────────────

    public async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var id = session.Transport.ReserveOrderId();
        await session.Transport.PlaceOrderAsync(Request(id, session.AccountId, request.ClientOrderId, request.Symbol, request.Side,
            request.Type, request.TimeInForce, request.Quantity, request.LimitPrice, request.StopPrice), ct).ConfigureAwait(false);
        return new RouteOrder(id.ToString(CultureInfo.InvariantCulture), request.ClientOrderId, request.Symbol, request.Side, request.Type,
            request.TimeInForce, request.Quantity, request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m,
            string.Empty, null, _time.GetUtcNow().UtcDateTime);
    }

    internal static InteractiveBrokersOrderRequest Request(
        int id, string account, string clientOrderId, string symbol, OrderSide side, RouteOrderType type, RouteTimeInForce timeInForce,
        decimal quantity, decimal? limit, decimal? stop)
    {
        if (!RouteValues.TryQuantity(quantity, out var exactQuantity))
            throw new BrokerOrderRouteException($"Interactive Brokers: {quantity} cannot be sent exactly.", isRejection: true);
        ScaledPrice? exactLimit = null;
        ScaledPrice? exactStop = null;
        if (limit is { } l && RouteValues.TryPrice(l, out var lp)) exactLimit = lp;
        if (stop is { } s && RouteValues.TryPrice(s, out var sp)) exactStop = sp;
        return new InteractiveBrokersOrderRequest(
            id, clientOrderId, account, Contract(symbol), side == OrderSide.Buy ? "BUY" : "SELL",
            type switch
            {
                RouteOrderType.Limit => InteractiveBrokersNativeOrderType.Limit,
                RouteOrderType.Stop => InteractiveBrokersNativeOrderType.Stop,
                RouteOrderType.StopLimit => InteractiveBrokersNativeOrderType.StopLimit,
                _ => InteractiveBrokersNativeOrderType.Market,
            },
            timeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
                RouteTimeInForce.ImmediateOrCancel => InteractiveBrokersNativeTimeInForce.ImmediateOrCancel,
                RouteTimeInForce.FillOrKill => InteractiveBrokersNativeTimeInForce.FillOrKill,
                _ => InteractiveBrokersNativeTimeInForce.Day,
            },
            exactQuantity, exactLimit, exactStop, null, null, OutsideRegularTradingHours: false);
    }

    public async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        await session.Transport.CancelOrderAsync(int.Parse(order.OrderId, CultureInfo.InvariantCulture), order.ClientOrderId, ct).ConfigureAwait(false);
    }

    public async Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var id = int.Parse(order.OrderId, CultureInfo.InvariantCulture);
        await session.Transport.ModifyOrderAsync(Request(id, session.AccountId, order.ClientOrderId, order.Symbol, order.Side, order.Type,
            order.TimeInForce, quantity, limitPrice, stopPrice), ct).ConfigureAwait(false);
        return order with { Quantity = quantity, LimitPrice = limitPrice, StopPrice = stopPrice, UpdatedAtUtc = _time.GetUtcNow().UtcDateTime };
    }

    public async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var snapshot = await session.Transport.GetReconciliationSnapshotAsync(session.AccountId, ct).ConfigureAwait(false);
        return [.. snapshot.OpenOrders.Where(o => Same(o.Contract, symbol)).Select(o => ReadOrder(o, symbol, session))];
    }

    public async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (!int.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return null;
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var snapshot = await session.Transport.GetReconciliationSnapshotAsync(session.AccountId, ct).ConfigureAwait(false);
        var order = snapshot.OpenOrders.Concat(snapshot.CompletedOrders).LastOrDefault(o => o.OrderId == id);
        if (order is not null)
            return ReadOrder(order, symbol, session);
        return session.Refusals.TryGetValue(id, out var refusal)
            ? new RouteOrder(orderId, clientOrderId, symbol, OrderSide.Buy, RouteOrderType.Market, RouteTimeInForce.Day, 0m, null, null,
                RouteOrderStatus.Rejected, 0m, null, 0m, string.Empty, refusal, _time.GetUtcNow().UtcDateTime)
            : null;
    }

    private static RouteOrder ReadOrder(InteractiveBrokersOrderSnapshot o, string symbol, Session session)
    {
        var filled = Dec(o.FilledQuantity);
        decimal? average = session.Fills.TryGetValue(o.OrderId, out var fill) && fill.Quantity == filled ? fill.Average : null;
        var status = o.Status switch
        {
            InteractiveBrokersNativeOrderStatus.Filled => RouteOrderStatus.Filled,
            InteractiveBrokersNativeOrderStatus.Cancelled or InteractiveBrokersNativeOrderStatus.ApiCancelled => RouteOrderStatus.Cancelled,
            InteractiveBrokersNativeOrderStatus.Rejected or InteractiveBrokersNativeOrderStatus.Inactive => RouteOrderStatus.Rejected,
            InteractiveBrokersNativeOrderStatus.PendingCancel => RouteOrderStatus.PendingCancel,
            InteractiveBrokersNativeOrderStatus.PendingSubmit => RouteOrderStatus.PendingNew,
            InteractiveBrokersNativeOrderStatus.PreSubmitted or InteractiveBrokersNativeOrderStatus.Submitted =>
                filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        return new RouteOrder(
            o.OrderId.ToString(CultureInfo.InvariantCulture),
            o.ClientOrderId,
            symbol,
            string.Equals(o.Side, "SELL", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
            o.OrderType switch
            {
                InteractiveBrokersNativeOrderType.Limit => RouteOrderType.Limit,
                InteractiveBrokersNativeOrderType.Stop => RouteOrderType.Stop,
                InteractiveBrokersNativeOrderType.StopLimit => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            o.TimeInForce switch
            {
                InteractiveBrokersNativeTimeInForce.GoodTillCancelled => RouteTimeInForce.GoodTillCancelled,
                InteractiveBrokersNativeTimeInForce.ImmediateOrCancel => RouteTimeInForce.ImmediateOrCancel,
                InteractiveBrokersNativeTimeInForce.FillOrKill => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            Dec(o.Quantity),
            o.LimitPrice is { } limit ? Dec(limit) : null,
            o.StopPrice is { } stop ? Dec(stop) : null,
            status,
            filled,
            average,
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected ? o.RejectionReason ?? (session.Refusals.TryGetValue(o.OrderId, out var why) ? why : null) : null,
            DateTime.SpecifyKind(o.UpdatedAtUtc, DateTimeKind.Utc));
    }

    public async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var session = await SessionAsync(environment, ct).ConfigureAwait(false);
        var snapshot = await session.Transport.GetReconciliationSnapshotAsync(session.AccountId, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, snapshot.Positions.Where(p => Same(p.Contract, symbol)).Sum(p => Dec(p.Quantity)));
    }

    /// <summary>None on this transport: the engine reads the terminal's own market data for the book's instrument.</summary>
    public Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) => Task.FromResult<RoutePrice?>(null);

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
