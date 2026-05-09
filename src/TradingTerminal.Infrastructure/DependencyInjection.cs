using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.NinjaTrader;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the broker layer (IB + NinjaTrader), market-data repository, connection
    /// manager, event bus, and UI dispatcher.
    ///
    /// Per-broker, the real client is wired only when its native binary resolved at
    /// build time (<c>HAS_IBAPI</c> / <c>HAS_NTAPI</c>) AND the corresponding
    /// <c>UseRealClient</c> option is true. Otherwise the synthetic fallback is used.
    /// </summary>
    public static IServiceCollection AddTradingTerminalInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IUiDispatcher, WpfDispatcher>();
        services.TryAddSingleton<IEventBus, EventBus>();
        services.TryAddSingleton<SessionContext>();

        // ---- Interactive Brokers ----
        services.AddSingleton<IBrokerClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<InteractiveBrokersOptions>>().Value;
#if HAS_IBAPI
            if (opt.UseRealClient)
                return ActivatorUtilities.CreateInstance<RealIbClient>(sp);
#endif
            return ActivatorUtilities.CreateInstance<FakeIbClient>(sp);
        });

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<InteractiveBrokersOptions>>().Value;
#if HAS_IBAPI
            if (opt.UseRealClient)
                return new BrokerConnectionMode(
                    BrokerKind.InteractiveBrokers,
                    IsLive: true,
                    DisplayName: "Live TWS",
                    Description: "Connected through the real TWS API. Make sure you're already signed in to TWS / IB Gateway (including 2FA).");
#endif
            return new BrokerConnectionMode(
                BrokerKind.InteractiveBrokers,
                IsLive: false,
                DisplayName: "IB Demo",
                Description: opt.UseRealClient
                    ? "UseRealClient is enabled but lib/CSharpAPI.dll wasn't present at build time — falling back to synthetic data."
                    : "Synthetic data — set UseRealClient=true in appsettings.json and place lib/CSharpAPI.dll to use real TWS.");
        });

        // ---- NinjaTrader ----
        services.AddSingleton<IBrokerClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<NinjaTraderOptions>>().Value;
#if HAS_NTAPI
            if (opt.UseRealClient)
                return ActivatorUtilities.CreateInstance<RealNinjaClient>(sp);
#endif
            return ActivatorUtilities.CreateInstance<FakeNinjaClient>(sp);
        });

        services.AddSingleton<BrokerConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<NinjaTraderOptions>>().Value;
#if HAS_NTAPI
            if (opt.UseRealClient)
                return new BrokerConnectionMode(
                    BrokerKind.NinjaTrader,
                    IsLive: true,
                    DisplayName: "Live NinjaTrader",
                    Description: "Connected through NTDirect.dll. NinjaTrader 8 must already be running with the AT Interface enabled.");
#endif
            return new BrokerConnectionMode(
                BrokerKind.NinjaTrader,
                IsLive: false,
                DisplayName: "NT Demo",
                Description: opt.UseRealClient
                    ? "UseRealClient is enabled but NTDirect.dll wasn't present at build time — falling back to synthetic data."
                    : "Synthetic data — install NinjaTrader 8 and set NinjaTrader:UseRealClient=true to use the real bridge.");
        });

        // ---- Selector + connection plumbing ----
        services.AddSingleton<IBrokerSelector, BrokerSelector>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        return services;
    }
}
