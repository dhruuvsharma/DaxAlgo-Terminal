using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Infrastructure.Ib;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the IB layer, market-data repository, connection manager, event bus,
    /// and UI dispatcher. The real IB client is wired only when <c>HAS_IBAPI</c> is
    /// defined (i.e. <c>lib/IBApi.dll</c> is present at build time) AND
    /// <c>InteractiveBrokers:UseRealClient = true</c>.
    /// </summary>
    public static IServiceCollection AddTradingTerminalInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton<IUiDispatcher, WpfDispatcher>();
        services.TryAddSingleton<IEventBus, EventBus>();
        services.TryAddSingleton<SessionContext>();

        services.AddSingleton<IIbClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<InteractiveBrokersOptions>>().Value;
#if HAS_IBAPI
            if (opt.UseRealClient)
                return ActivatorUtilities.CreateInstance<RealIbClient>(sp);
#endif
            return ActivatorUtilities.CreateInstance<FakeIbClient>(sp);
        });

        services.AddSingleton<IbConnectionMode>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<InteractiveBrokersOptions>>().Value;
#if HAS_IBAPI
            if (opt.UseRealClient)
                return new IbConnectionMode(
                    IsLive: true,
                    DisplayName: "Live TWS",
                    Description: "Connected through the real TWS API. Make sure you're already signed in to TWS / IB Gateway (including 2FA).");
#endif
            return new IbConnectionMode(
                IsLive: false,
                DisplayName: "Demo mode",
                Description: opt.UseRealClient
                    ? "UseRealClient is enabled but lib/IBApi.dll wasn't present at build time — falling back to synthetic data."
                    : "Synthetic data — set UseRealClient=true in appsettings.json and place lib/IBApi.dll to use real TWS.");
        });

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        return services;
    }
}
