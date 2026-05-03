using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
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

        services.AddSingleton<IIbClient>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<InteractiveBrokersOptions>>().Value;
#if HAS_IBAPI
            if (opt.UseRealClient)
                return ActivatorUtilities.CreateInstance<RealIbClient>(sp);
#endif
            return ActivatorUtilities.CreateInstance<FakeIbClient>(sp);
        });

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<IMarketDataRepository, MarketDataRepository>();

        return services;
    }
}
