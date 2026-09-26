using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Execution;
using TradingTerminal.Execution.CTrader;

namespace TradingTerminal.Execution.Routing;

/// <summary>
/// Registration for routed brokers: the shared options, and the cTrader route. The other routes are registered
/// by the infrastructure layer as <c>IBrokerOrderRoute</c>; cTrader's lives here because its Open API
/// transport does, and the execution console turns each route into a broker card.
/// </summary>
public static class RoutedExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RoutedExecutionOptions"/>. Nothing is armed: every routed card starts in PAPER
    /// (or disabled, for a broker without one), and LIVE needs the owner option, stored credentials and the
    /// typed confirmation for the exact account.
    /// </summary>
    public static IServiceCollection AddRoutedExecution(this IServiceCollection services, Action<RoutedExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RoutedExecutionOptions();
        configure?.Invoke(options);
        var fault = options.Validate();
        if (fault is not null)
            throw new InvalidOperationException($"{RoutedExecutionOptions.SectionName}: {fault}");
        services.AddSingleton(options.Snapshot());
        // cTrader through the login row's credentials — demo as PAPER, live behind the gate (2026-09-25).
        services.AddSingleton<IBrokerOrderRoute, CTraderOrderRoute>();
        // Interactive Brokers through TWS or IB Gateway where the IB login row says it listens (2026-09-25).
        services.AddSingleton<IBrokerOrderRoute, TradingTerminal.Execution.InteractiveBrokers.InteractiveBrokersOrderRoute>();
        return services;
    }
}
