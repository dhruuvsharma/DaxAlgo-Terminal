using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
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

#if HAS_IBAPI
        services.AddSingleton<IBrokerClient>(sp =>
            ActivatorUtilities.CreateInstance<RealIbClient>(sp));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.InteractiveBrokers,
                IsLive: true,
                DisplayName: "Interactive Brokers",
                Description: "Connected through the real TWS API. Make sure TWS / IB Gateway is signed in (including 2FA) before connecting."));
#endif

#if HAS_NTAPI
        services.AddSingleton<IBrokerClient>(sp =>
            ActivatorUtilities.CreateInstance<RealNinjaClient>(sp));

        services.AddSingleton<BrokerConnectionMode>(_ =>
            new BrokerConnectionMode(
                BrokerKind.NinjaTrader,
                IsLive: true,
                DisplayName: "NinjaTrader",
                Description: "Connected through NTDirect.dll. NinjaTrader 8 must be running with the AT Interface enabled."));
#endif

        // cTrader — always available.
        services.AddSingleton<IBrokerClient>(sp =>
            ActivatorUtilities.CreateInstance<RealCTraderClient>(sp));

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

        services.AddSingleton<IBrokerSelector, BrokerSelector>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        // Trading seam.
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<IOrderRouter, LiveOrderRouter>();

        return services;
    }
}
