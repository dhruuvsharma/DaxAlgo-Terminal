using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.CTrader;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Core.Time;
using TradingTerminal.Infrastructure.Alpaca;
using TradingTerminal.Infrastructure.Binance;
using TradingTerminal.Infrastructure.CTrader;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.IronBeam;
using TradingTerminal.Infrastructure.LondonStrategicEdge;
using TradingTerminal.Infrastructure.Upstox;
using TradingTerminal.Core.Brokers.Upstox;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Simulation;
#if HAS_NTAPI
using TradingTerminal.Infrastructure.NinjaTrader;
#endif
using TradingTerminal.Infrastructure.Threading;
using TradingTerminal.Infrastructure.Time;

namespace TradingTerminal.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the broker layer (real clients only — no synthetic fallbacks), market-data
    /// repository, connection manager, event bus, and UI dispatcher.
    ///
    /// Per-broker availability:
    ///   IB   — registered when <c>HAS_IBAPI</c> is defined (CSharpAPI.dll resolved at build).
    ///   NT   — registered when <c>HAS_NTAPI</c> is defined (NTDirect.dll resolved at build).
    ///   CT   — always registered (cTrader.OpenAPI.Net is a NuGet reference, always restored).
    ///
    /// "Live or paper" is the broker's own concept (TWS port 7497 vs 7496, cTrader demo vs live
    /// endpoint, NT Sim101 vs funded account) — the connection itself is always real.
    /// </summary>
    public static IServiceCollection AddTradingTerminalInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IUiDispatcher, WpfDispatcher>();
        services.TryAddSingleton<IEventBus, EventBus>();
        services.TryAddSingleton<SessionContext>();

        // API-call meter — singleton, used by every broker via MeteredBrokerClient decorator
        // below, polled by the header chip widget in the WPF shell.
        services.AddSingleton<IBrokerApiMeter, BrokerApiMeter>();

#if HAS_IBAPI
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealIbClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.InteractiveBrokers,
                IsLive: true,
                DisplayName: "Interactive Brokers",
                Description: "Connected through the real TWS API. Make sure TWS / IB Gateway is signed in (including 2FA) before connecting."));
#endif

#if HAS_NTAPI
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealNinjaClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.NinjaTrader,
                IsLive: true,
                DisplayName: "NinjaTrader",
                Description: "Connected through NTDirect.dll. NinjaTrader 8 must be running with the AT Interface enabled."));
#endif

        // cTrader — always available.
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealCTraderClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        // One-shot helper for the login form's "Discover accounts" button. Resolves the
        // ctidTraderAccountId from an access token so the user doesn't have to hunt it down.
        services.AddSingleton<ICTraderAccountDiscovery, CTraderAccountDiscoveryService>();

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CTraderOptions>>().Value;
            return new BrokerConnectionMode(
                BrokerKind.CTrader,
                IsLive: opt.IsLive,
                DisplayName: opt.IsLive ? "Live cTrader" : "Demo cTrader",
                Description: opt.IsLive
                    ? "Connected to live.ctraderapi.com via Spotware Open API."
                    : "Connected to demo.ctraderapi.com via Spotware Open API (paper account).");
        });

        // Alpaca — always available (REST + WebSocket SDK on NuGet, no DLL gate).
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealAlpacaClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<AlpacaOptions>>().Value;
            return new BrokerConnectionMode(
                BrokerKind.Alpaca,
                IsLive: opt.IsLive,
                DisplayName: opt.IsLive ? "Live Alpaca" : "Paper Alpaca",
                Description: opt.IsLive
                    ? "Connected to api.alpaca.markets (funded account)."
                    : "Connected to paper-api.alpaca.markets (paper trading).");
        });

        // Binance — always available (public market data over WebSocket + REST; no SDK, no key,
        // no account). Real, live crypto bars / L1 / L2 / trades — the zero-credential way to run
        // the terminal against a real feed. Metered like the other networked brokers.
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealBinanceClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.Binance,
                IsLive: true,
                DisplayName: "Binance (live data)",
                Description: "Public Binance market data — real, live crypto bars / L1 / L2 / trades. No API key, no account."));

        // Ironbeam — always available (futures FCM over a hand-rolled REST + WebSocket API v2; no SDK
        // DLL gate, just HTTP). JWT auth from username + API key, market data through a server-created
        // stream (L1 quotes / L2 depth / real trade tape). Demo or live by options. Metered like the
        // other networked brokers.
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealIronBeamClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<IronBeamOptions>>().Value;
            return new BrokerConnectionMode(
                BrokerKind.IronBeam,
                IsLive: opt.IsLive,
                DisplayName: opt.IsLive ? "Ironbeam · Live" : "Ironbeam · Demo",
                Description: "Futures (FCM) — REST + WebSocket API v2; demo or live by options");
        });

        // London Strategic Edge — always available (free multi-asset L1 ticks + historical OHLCV
        // over a single WebSocket + PostgREST-style REST; no SDK, just an API key). Data-only at
        // the provider (no order path exists). No depth; trade tape deliberately not wired until
        // the tick stream is verified to carry true prints. Metered like the other networked
        // brokers.
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealLondonStrategicEdgeClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.LondonStrategicEdge,
                IsLive: true,
                DisplayName: "London Strategic Edge",
                Description: "Free multi-asset market data — live L1 ticks + historical OHLCV for stocks, FX, crypto, commodities, indices, ETFs. 50 GB/month free tier."));

        // Upstox — always available (Indian-market broker over REST + WebSocket API v2/v3; no SDK, just
        // HTTP). OAuth2 access token from the login form, live ticks + 5-level depth over the V3
        // protobuf market-data feed, historical candles + instrument master over REST. No real trade
        // tape (feed carries LTP + book). Metered like the other networked brokers.
        services.AddSingleton<IBrokerClient>(sp =>
            new MeteredBrokerClient(
                ActivatorUtilities.CreateInstance<RealUpstoxClient>(sp),
                sp.GetRequiredService<IBrokerApiMeter>()));

        // One-shot helper for the login form's OAuth2 authorization-code exchange.
        services.AddSingleton<IUpstoxAuthService, UpstoxAuthService>();

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.Upstox,
                IsLive: true,
                DisplayName: "Upstox",
                Description: "Indian markets (NSE/BSE) — REST + WebSocket API v2/v3; OAuth2, live L1 + 5-level depth, historical candles. Data-only."));

        // Simulated — always available (in-process, no SDK, no network). Backs BrokerKind.Simulated
        // for the offline dev launch profiles: a synthetic random-walk feed, or replay of the local
        // store. Not wrapped in MeteredBrokerClient — there are no external API calls to count.
        services.AddSingleton<IBrokerClient>(sp =>
            ActivatorUtilities.CreateInstance<SimulatedBrokerClient>(sp));

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<SimulatedBrokerOptions>>().Value;
            var replay = opt.Mode == SimulatedFeedMode.Replay;
            return new BrokerConnectionMode(
                BrokerKind.Simulated,
                IsLive: false,
                DisplayName: replay ? "Simulated (replay)" : "Simulated (synthetic)",
                Description: replay
                    ? "Replays recorded data from the local store as a live feed — offline, no broker."
                    : "In-process random-walk feed. Fully offline — no broker, no network.");
        });

        services.AddSingleton<IBrokerSelector, BrokerSelector>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        // Clock seam — shared by the backtest engine and live signal-timing.
        services.TryAddSingleton<IClock, SystemClock>();

        // Read-only analytical query layer over the Parquet tick archive (DuckDB, embedded).
        services.TryAddSingleton<IParquetQueryService, DuckDbParquetQueryService>();

        return services;
    }
}
