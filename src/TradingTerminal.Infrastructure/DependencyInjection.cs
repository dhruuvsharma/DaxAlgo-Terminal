using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Brokers.CTrader;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Alpaca;
using TradingTerminal.Infrastructure.CTrader;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.MarketData;
#if HAS_NTAPI
using TradingTerminal.Infrastructure.NinjaTrader;
#endif
using TradingTerminal.Infrastructure.Threading;
using TradingTerminal.Infrastructure.Time;
using TradingTerminal.Infrastructure.Trading;

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

        services.AddSingleton<IBrokerSelector, BrokerSelector>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        // Trading seam.
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<IOrderRouter, LiveOrderRouter>();

        return services;
    }
}
