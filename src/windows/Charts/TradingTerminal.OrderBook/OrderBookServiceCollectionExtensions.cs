using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.OrderBook;

/// <summary>DI registration for the standalone Order Book tool. Transient so each open gets a fresh
/// VM (and depth subscription) that disposes with the window.</summary>
public static class OrderBookServiceCollectionExtensions
{
    public static IServiceCollection AddOrderBookSurface(this IServiceCollection services)
    {
        services.AddTransient<OrderBookViewModel>();
#if WINDOWS
        services.AddTransient<OrderBookWindow>();
#endif
        return services;
    }
}
