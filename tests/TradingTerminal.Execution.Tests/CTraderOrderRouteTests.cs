using Google.Protobuf;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution;
using TradingTerminal.Execution.CTrader;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// The cTrader order route against a scripted Open API peer: the sign-in sequence it sends, how it reads a
/// symbol's rules, how it spells an order, how a hedged account's opposite position is closed rather than
/// doubled, and what counts as a refusal. What it cannot prove is that cTrader agrees — that is the owner's
/// demo test.
/// </summary>
public sealed class CTraderOrderRouteTests
{
    private const long DemoAccount = 4_600_001;
    private const long LiveAccount = 4_600_002;
    private const long EurUsd = 1;

    private sealed class Keys(BrokerCredential credential) : IBrokerCredentialSource
    {
        public BrokerCredential For(BrokerKind broker) => broker == BrokerKind.CTrader ? credential : BrokerCredential.None;
    }

    private static Keys Stored(long account = DemoAccount, bool live = false) =>
        new(new BrokerCredential("client-1", "secret-1")
        {
            Session = "token-1",
            Account = account.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Extra = live ? "live" : "demo",
        });

    /// <summary>Answers each request from a script and records what was asked.</summary>
    private sealed class Peer : ICTraderExecutionTransport
    {
        private readonly Func<IMessage, IMessage?> _answer;

        public Peer(CTraderExecutionEndpoint endpoint, Func<IMessage, IMessage?> answer)
        {
            Endpoint = endpoint;
            _answer = answer;
        }

        public CTraderExecutionEndpoint Endpoint { get; }

        public bool IsConnected { get; private set; }

        public List<IMessage> Seen { get; } = [];

        public event Action<ProtoMessage>? MessageReceived;

        public event Action<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(ProtoMessage message, CancellationToken cancellationToken = default)
        {
            var request = CTraderOpenApiProtocol.Decode(message)!;
            lock (Seen)
                Seen.Add(request);
            if (_answer(request) is { } answer)
                MessageReceived?.Invoke(CTraderOpenApiProtocol.Encode(answer, message.ClientMsgId));
            if (request is ProtoOASubscribeSpotsReq subscribe)
            {
                MessageReceived?.Invoke(CTraderOpenApiProtocol.Encode(new ProtoOASpotEvent
                {
                    CtidTraderAccountId = subscribe.CtidTraderAccountId,
                    SymbolId = subscribe.SymbolId[0],
                    Bid = 108_450,
                    Ask = 108_470,
                }));
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A demo account with EURUSD (step 0.01 lot = 1,000 units, 5 digits), a long position of 2,000
    /// units, and whatever <paramref name="order"/> answers to an order.</summary>
    private static Func<IMessage, IMessage?> Script(Func<IMessage, IMessage?>? order = null) => request => request switch
    {
        ProtoOAApplicationAuthReq => new ProtoOAApplicationAuthRes(),
        ProtoOAGetAccountListByAccessTokenReq => Accounts(),
        ProtoOAAccountAuthReq auth => new ProtoOAAccountAuthRes { CtidTraderAccountId = auth.CtidTraderAccountId },
        ProtoOATraderReq trader => new ProtoOATraderRes
        {
            CtidTraderAccountId = trader.CtidTraderAccountId,
            Trader = new ProtoOATrader
            {
                CtidTraderAccountId = trader.CtidTraderAccountId,
                Balance = 1_000_050,
                MoneyDigits = 2,
                DepositAssetId = 15,
                AccountType = ProtoOAAccountType.Hedged,
            },
        },
        ProtoOAAssetListReq assets => AssetList(assets.CtidTraderAccountId),
        ProtoOASymbolsListReq symbols => SymbolList(symbols.CtidTraderAccountId),
        ProtoOASymbolByIdReq byId => SymbolById(byId.CtidTraderAccountId),
        ProtoOAReconcileReq reconcile => Reconcile(reconcile.CtidTraderAccountId),
        ProtoOASubscribeSpotsReq spots => new ProtoOASubscribeSpotsRes { CtidTraderAccountId = spots.CtidTraderAccountId },
        _ => order?.Invoke(request),
    };

    private static ProtoOAGetAccountListByAccessTokenRes Accounts()
    {
        var accounts = new ProtoOAGetAccountListByAccessTokenRes { PermissionScope = ProtoOAClientPermissionScope.ScopeTrade };
        accounts.CtidTraderAccount.Add(new ProtoOACtidTraderAccount { CtidTraderAccountId = (ulong)DemoAccount, IsLive = false });
        accounts.CtidTraderAccount.Add(new ProtoOACtidTraderAccount { CtidTraderAccountId = (ulong)LiveAccount, IsLive = true });
        return accounts;
    }

    private static ProtoOAAssetListRes AssetList(long account)
    {
        var assets = new ProtoOAAssetListRes { CtidTraderAccountId = account };
        assets.Asset.Add(new ProtoOAAsset { AssetId = 15, Name = "USD" });
        assets.Asset.Add(new ProtoOAAsset { AssetId = 16, Name = "EUR" });
        return assets;
    }

    private static ProtoOASymbolsListRes SymbolList(long account)
    {
        var list = new ProtoOASymbolsListRes { CtidTraderAccountId = account };
        list.Symbol.Add(new ProtoOALightSymbol { SymbolId = EurUsd, SymbolName = "EURUSD", Enabled = true, BaseAssetId = 16, QuoteAssetId = 15 });
        return list;
    }

    private static ProtoOASymbolByIdRes SymbolById(long account)
    {
        var answer = new ProtoOASymbolByIdRes { CtidTraderAccountId = account };
        answer.Symbol.Add(new ProtoOASymbol
        {
            SymbolId = EurUsd,
            Digits = 5,
            MinVolume = 100_000,
            MaxVolume = 1_000_000_000,
            StepVolume = 100_000,
            TradingMode = ProtoOATradingMode.Enabled,
        });
        return answer;
    }

    private static ProtoOAReconcileRes Reconcile(long account)
    {
        var reconcile = new ProtoOAReconcileRes { CtidTraderAccountId = account };
        reconcile.Position.Add(new ProtoOAPosition
        {
            PositionId = 77,
            PositionStatus = ProtoOAPositionStatus.PositionStatusOpen,
            TradeData = new ProtoOATradeData { SymbolId = EurUsd, Volume = 200_000, TradeSide = ProtoOATradeSide.Buy },
        });
        return reconcile;
    }

    private static (CTraderOrderRoute Route, List<Peer> Peers) Route(Keys keys, Func<IMessage, IMessage?> script)
    {
        var peers = new List<Peer>();
        var route = new CTraderOrderRoute(keys, endpoint =>
        {
            var peer = new Peer(endpoint, script);
            peers.Add(peer);
            return peer;
        }, TimeProvider.System, TimeSpan.FromSeconds(5));
        return (route, peers);
    }

    [Fact]
    public async Task Demo_is_paper_and_the_account_and_cash_come_from_the_trader()
    {
        var (route, peers) = Route(Stored(), Script());
        await using var _ = route;

        var account = await route.ConnectAsync(RouteEnvironment.Paper, CancellationToken.None);

        Assert.Equal(CTraderExecutionOptions.DemoHost, Assert.Single(peers).Endpoint.Host);
        Assert.Equal(DemoAccount.ToString(System.Globalization.CultureInfo.InvariantCulture), account.AccountId);
        Assert.Equal("USD", account.Currency);
        Assert.Equal(10_000.50m, account.CashTotal);
        var auth = Assert.Single(peers[0].Seen.OfType<ProtoOAApplicationAuthReq>());
        Assert.Equal(("client-1", "secret-1"), (auth.ClientId, auth.ClientSecret));
        Assert.Equal("token-1", Assert.Single(peers[0].Seen.OfType<ProtoOAAccountAuthReq>()).AccessToken);
        Assert.Equal("DEMO", route.PaperEnvironmentName);
    }

    [Fact]
    public async Task A_demo_account_is_refused_for_a_live_card_before_anything_is_sent_for_it()
    {
        var (route, _) = Route(Stored(DemoAccount), Script());
        await using var __ = route;

        var refused = await Assert.ThrowsAsync<BrokerOrderRouteException>(() => route.ConnectAsync(RouteEnvironment.Live, CancellationToken.None));

        Assert.True(refused.IsRejection);
        Assert.Contains("demo account", refused.Message, StringComparison.Ordinal);
        Assert.Contains("this card trades live", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_symbol_is_found_by_name_and_its_engine_unit_is_the_volume_step()
    {
        var (route, _) = Route(Stored(), Script());
        await using var __ = route;

        var rules = await route.InstrumentAsync(RouteEnvironment.Paper, "EUR/USD", CancellationToken.None);

        Assert.True(rules.IsValid);
        Assert.Equal(1_000m, rules.UnitSize);
        Assert.Equal(0.00001m, rules.TickSize);
        Assert.Equal(1, rules.MinimumUnits);
        Assert.Equal(10_000, rules.MaximumUnits);
        Assert.Equal("USD", rules.Currency);
        Assert.True(rules.SupportsReplace);
        Assert.Equal(2_000m, (await route.PositionAsync(RouteEnvironment.Paper, "EURUSD", CancellationToken.None)).Quantity);
    }

    [Fact]
    public async Task A_sell_market_order_on_a_hedged_account_closes_the_open_long_instead_of_opening_a_short()
    {
        ProtoOANewOrderReq? sent = null;
        var (route, _) = Route(Stored(), Script(request =>
        {
            if (request is not ProtoOANewOrderReq order)
                return null;
            sent = order;
            return new ProtoOAExecutionEvent
            {
                CtidTraderAccountId = order.CtidTraderAccountId,
                ExecutionType = ProtoOAExecutionType.OrderAccepted,
                Order = new ProtoOAOrder
                {
                    OrderId = 5001,
                    OrderType = order.OrderType,
                    OrderStatus = ProtoOAOrderStatus.OrderStatusAccepted,
                    ClientOrderId = order.ClientOrderId,
                    TradeData = new ProtoOATradeData { SymbolId = order.SymbolId, Volume = order.Volume, TradeSide = order.TradeSide },
                },
            };
        }));
        await using var __ = route;

        var placed = await route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("EURUSD", "daxr-a-1", OrderSide.Sell, RouteOrderType.Market, RouteTimeInForce.GoodTillCancelled, 1_000m, null, null),
            CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Equal(100_000, sent!.Volume);
        Assert.Equal(ProtoOATradeSide.Sell, sent.TradeSide);
        Assert.Equal(77, sent.PositionId);
        Assert.Equal("daxr-a-1", sent.ClientOrderId);
        Assert.False(sent.HasTimeInForce, "a market order carries no time in force");
        Assert.Equal(("5001", RouteOrderStatus.Working, 1_000m), (placed.OrderId, placed.Status, placed.Quantity));
    }

    [Fact]
    public async Task An_order_error_is_a_refusal_in_ctraders_words()
    {
        var (route, _) = Route(Stored(), Script(request => request is ProtoOANewOrderReq order
            ? new ProtoOAOrderErrorEvent
            {
                CtidTraderAccountId = order.CtidTraderAccountId,
                ErrorCode = "NOT_ENOUGH_MONEY",
                Description = "Not enough money",
            }
            : null));
        await using var __ = route;

        var refused = await Assert.ThrowsAsync<BrokerOrderRouteException>(() => route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("EURUSD", "daxr-a-2", OrderSide.Buy, RouteOrderType.Limit, RouteTimeInForce.GoodTillCancelled, 1_000m, 1.0801m, null),
            CancellationToken.None));

        Assert.True(refused.IsRejection);
        Assert.Contains("Not enough money (NOT_ENOUGH_MONEY)", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_is_an_unknown_outcome_not_a_refusal()
    {
        var peers = new List<Peer>();
        var route = new CTraderOrderRoute(Stored(), endpoint =>
        {
            var peer = new Peer(endpoint, Script());
            peers.Add(peer);
            return peer;
        }, TimeProvider.System, TimeSpan.FromMilliseconds(200));
        await using var __ = route;

        var exception = await Record.ExceptionAsync(() => route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("EURUSD", "daxr-a-3", OrderSide.Buy, RouteOrderType.Limit, RouteTimeInForce.GoodTillCancelled, 1_000m, 1.0801m, null),
            CancellationToken.None));

        Assert.NotNull(exception);
        Assert.IsNotType<BrokerOrderRouteException>(exception);
    }

    [Fact]
    public async Task A_stop_limit_carries_its_limit_as_slippage_in_points_from_the_stop()
    {
        ProtoOANewOrderReq? sent = null;
        var (route, _) = Route(Stored(), Script(request =>
        {
            if (request is not ProtoOANewOrderReq order)
                return null;
            sent = order;
            return new ProtoOAExecutionEvent
            {
                CtidTraderAccountId = order.CtidTraderAccountId,
                Order = new ProtoOAOrder
                {
                    OrderId = 5002,
                    OrderType = order.OrderType,
                    OrderStatus = ProtoOAOrderStatus.OrderStatusAccepted,
                    TradeData = new ProtoOATradeData { SymbolId = order.SymbolId, Volume = order.Volume, TradeSide = order.TradeSide },
                },
            };
        }));
        await using var __ = route;

        await route.SubmitAsync(RouteEnvironment.Paper,
            new RouteOrderRequest("EURUSD", "daxr-a-4", OrderSide.Buy, RouteOrderType.StopLimit, RouteTimeInForce.GoodTillCancelled, 2_000m, 1.08520m, 1.08500m),
            CancellationToken.None);

        Assert.Equal(ProtoOAOrderType.StopLimit, sent!.OrderType);
        Assert.Equal(1.085, sent.StopPrice, 10);
        Assert.Equal(20, sent.SlippageInPoints);
        Assert.Equal(200_000, sent.Volume);
        Assert.Equal(ProtoOATimeInForce.GoodTillCancel, sent.TimeInForce);
    }

    [Fact]
    public async Task The_reference_price_is_the_mid_of_the_first_spot_after_subscribing()
    {
        var (route, peers) = Route(Stored(), Script());
        await using var __ = route;

        var price = await route.PriceAsync(RouteEnvironment.Paper, "EURUSD", CancellationToken.None);

        Assert.NotNull(price);
        Assert.Equal(1.0846m, price!.Price);
        Assert.Equal(DateTimeKind.Utc, price.ObservedAtUtc.Kind);
        Assert.Single(peers[0].Seen.OfType<ProtoOASubscribeSpotsReq>());
        _ = await route.PriceAsync(RouteEnvironment.Paper, "EURUSD", CancellationToken.None);
        Assert.Single(peers[0].Seen.OfType<ProtoOASubscribeSpotsReq>());
    }

    [Fact]
    public async Task The_generic_adapter_binds_a_ctrader_book_in_whole_volume_steps()
    {
        // The route under the same adapter every routed broker uses: the card connects, a book binds to EURUSD,
        // and its unit is the 1,000-unit volume step with the 2,000 units already held as its baseline.
        var (route, _) = Route(Stored(), Script());
        await using var __ = route;
        var adapter = new TradingTerminal.Execution.Routing.RoutedExecutionAdapter(
            route, ExecutionMode.Paper, new TradingTerminal.Execution.Routing.RoutedExecutionOptions(), hasCredentials: true,
            new FixedClock(DateTime.UtcNow));
        await using var ___ = adapter;
        await adapter.ConnectAsync(CancellationToken.None);
        await using var book = adapter.CreateBookAdapter(new InstrumentId(9001), "EURUSD");
        await book.ConnectAsync(CancellationToken.None);

        Assert.Equal("ctrader-paper", adapter.AdapterId);
        Assert.Equal(1_000m, book.Rules!.UnitSize);
        Assert.Equal(2_000m, book.BaselinePosition);
    }

    private sealed class FixedClock(DateTime utc) : TradingTerminal.Core.Time.IClock
    {
        public DateTime UtcNow { get; } = utc;
    }
}

/// <summary>The replicator scales a strategy's model position by the book's size.</summary>
public sealed class SandboxReplicationSizeTests
{
    private sealed class Intake : IExecutionBookTargetIntake
    {
        public List<TradeIntent> Received { get; } = [];

        public ValueTask<ExecutionTargetSubmissionResult> SubmitTargetAsync(string bookId, TradeIntent intent, CancellationToken cancellationToken = default)
        {
            lock (Received)
                Received.Add(intent);
            return ValueTask.FromResult(ExecutionTargetSubmissionResult.Success("taken"));
        }
    }

    private sealed class Source(IModelPortfolio snapshot) : IModelPortfolioSource
    {
        public IModelPortfolio? CurrentSnapshot { get; } = snapshot;

        public event Action<IModelPortfolio>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    [Fact]
    public async Task Three_strategy_units_on_a_book_of_size_five_are_a_fifteen_unit_target()
    {
        var intake = new Intake();
        var snapshot = new SandboxPortfolioSnapshot(new InstrumentId(9001), 3d, 3d, 1.08d, 1, 100_000d, 0d, 0d, 0d, 100_000d, 0d, 0, 0, 0, 0, 0, true);
        var done = new TaskCompletionSource<SandboxExecutionReplicationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var replicator = new SandboxExecutionReplicator(new Source(snapshot), intake,
            new SandboxExecutionReplicationOptions("book-1", "EMA Cross", UnitsPerStrategyUnit: 5));
        replicator.SubmissionCompleted += outcome => done.TrySetResult(outcome);
        replicator.ReplicateCurrent();

        var outcome = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(outcome.Result.IsSuccess);
        Assert.True(outcome.Intent!.Value.SignedUnits.TryGetWholeUnits(out var units));
        Assert.Equal(15, units);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SandboxExecutionReplicator(new Source(snapshot), intake,
            new SandboxExecutionReplicationOptions("book-1", "EMA Cross", UnitsPerStrategyUnit: 0)));
    }
}
